extern alias unitycore;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using AzureArchive.VideoTools.Core.Commands;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Color = unitycore::UnityEngine.Color;

namespace AzureArchive.VideoTools.Interop;

internal sealed class EditorSceneService : IEditorSceneService
{
    private readonly RuntimeCapabilityService _capabilities;
    private readonly ScenarioFileService _files;

    public EditorSceneService(RuntimeCapabilityService capabilities, ScenarioFileService files)
    {
        _capabilities = capabilities;
        _files = files;
    }

    public ApiResult<InspectorReferenceProbe> ProbeInspectorReference()
    {
        try
        {
            ScriptNodeInspector? inspector = ScriptNodeInspector.instance;
            bool inspectorReturned = !ReferenceEquals(inspector, null);

            if (inspectorReturned)
            {
                _capabilities.Verified(
                    "Editor.InspectorSingleton",
                    "ScriptNodeInspector.instance returned a wrapper; no inspector members were read");
            }
            else
            {
                _capabilities.Bound(
                    "Editor.InspectorSingleton",
                    "ScriptNodeInspector.instance getter returned null in the current view");
            }

            ScriptNode? scriptNode = inspectorReturned ? inspector!.scriptNode : null;
            bool scriptNodeReturned = !ReferenceEquals(scriptNode, null);
            if (inspectorReturned)
            {
                _capabilities.Verified(
                    "Editor.InspectorScriptNode",
                    scriptNodeReturned
                        ? "ScriptNodeInspector.scriptNode returned a wrapper; no node members were read"
                        : "ScriptNodeInspector.scriptNode getter returned null; no node members were read");
            }

            return ApiResult<InspectorReferenceProbe>.Ok(
                new InspectorReferenceProbe(
                    inspectorReturned,
                    scriptNodeReturned,
                    "ScriptNodeInspector.instance -> scriptNode"));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.InspectorSingleton", detail);
            return ApiResult<InspectorReferenceProbe>.Fail(detail);
        }
    }

    public ApiResult<SceneSnapshot> GetSelectedScene()
    {
        try
        {
            if (!TryGetInternalSelection(out InternalSceneSelection selection, out string error))
            {
                return ApiResult<SceneSnapshot>.Fail(error);
            }

            return ReadInternalSelection(selection);
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.Selection", detail);
            return ApiResult<SceneSnapshot>.Fail(detail);
        }
    }

    // DataList rebuilds the visible rows before their selected flags settle. Only this
    // observation boundary may capture the unique row described by BOTH official inputs.
    // It never supplies a document transaction or authorizes execution. The later pump
    // must still resolve a genuinely selected row and confirm its context and both texts.
    internal ApiResult<SceneSnapshot> CaptureDataListScene(out EditorInputSceneCaptureProof? proof)
    {
        proof = null;
        ApiResult<SceneSnapshot> selected = GetSelectedScene();
        if (selected.Success || selected.Error != "请先在工作台选择一句台词。") return selected;
        try
        {
            var runtime = Rukari.Lib.ModServices.Current;
            if (runtime is not { State: Rukari.Lib.RuntimeState.Ready, IsMainThread: true }
                || !Rukari.Lib.Runtime.Editor.EditorWorkspaceContext.IsNodeEditorVisible)
                return selected;

            if (!_files.HasCanonicalProjectKey && !ActiveProjectPairSource.HasActivePair)
                LiveEditorProjectKeySource.TryAdoptFromStudioCommon(_files);
            EditorInputSceneFrame before = ReadInputSceneFrame(out InternalSceneSelection inputRow);
            ApiResult<SceneSnapshot> snapshot = ReadInternalSelection(inputRow);
            EditorInputSceneFrame after = ReadInputSceneFrame(out _);
            var captured = EditorInputSceneCapturePlanner.Capture(before, after);
            if (!captured.Success || captured.Value == null)
                return ApiResult<SceneSnapshot>.Fail(captured.Error);
            EditorInputSceneCaptureProof candidate = captured.Value;
            if (!snapshot.Success || snapshot.Value == null) return snapshot;
            SceneSnapshot scene = snapshot.Value;
            if (scene.Address.ProjectKey != candidate.ProjectKey
                || scene.Address.NodeGuid != candidate.NodeGuid
                || scene.Address.SceneIndex != candidate.SceneIndex
                || scene.DialogueText != candidate.DialogueText)
                return ApiResult<SceneSnapshot>.Fail("input-scene-snapshot-drift");
            proof = candidate;
            return snapshot;
        }
        catch (Exception ex)
        {
            return ApiResult<SceneSnapshot>.Fail("input-scene-capture: " + PatchGuard.Describe(ex));
        }
    }

    private EditorInputSceneFrame ReadInputSceneFrame(out InternalSceneSelection matchingRow)
    {
        matchingRow = default;
        ScriptNodeInspector? active = ResolveInspector();
        if (!InteropObjectGuard.IsAlive(active)) throw new InvalidOperationException("input-scene-inspector-unavailable");
        ScriptNodeInspector inspector = active!;
        ScriptNode node = inspector.scriptNode;
        var cache = inspector.scriptNodeListItemsCache;
        if (!InteropObjectGuard.IsAlive(node) || !InteropObjectGuard.IsAlive(cache)
            || inspector.rearrangeScheduled || cache.Count > EditorInputSceneCapturePlanner.MaximumCacheRows)
            throw new InvalidOperationException("input-scene-cache-unavailable");
        var scripts = node.scripts;
        UIInput content = inspector.contentInput;
        UIInput prompt = inspector.additionalPromptInput;
        if (!InteropObjectGuard.IsAlive(scripts) || !InteropObjectGuard.IsAlive(content)
            || !InteropObjectGuard.IsAlive(prompt))
            throw new InvalidOperationException("input-scene-inputs-unavailable");
        string? dialogueInput = content.value;
        string? promptInput = prompt.value;
        if (dialogueInput == null || promptInput == null)
            throw new InvalidOperationException("input-scene-input-read-unavailable");
        string nodeGuid = InteropMemberAccess.Get<object>(node, "guid")?.ToString() ?? string.Empty;
        int scriptCount = scripts.Count;
        var context = new EditorInputSceneFrameContext(inspector.Pointer.ToInt64(), node.Pointer.ToInt64(),
            cache.Pointer.ToInt64(), scripts.Pointer.ToInt64(), content.Pointer.ToInt64(), prompt.Pointer.ToInt64(),
            _files.Current.ProjectKey, nodeGuid, scriptCount, inspector.rearrangeScheduled);
        var rows = new List<EditorInputSceneRowProof>();
        int count = cache.Count;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            ScriptListItem row = cache[i];
            if (!InteropObjectGuard.IsAlive(row) || row.gameObject == null)
                throw new InvalidOperationException("input-scene-row-lifetime-unavailable");
            bool activeRow = row.gameObject.activeInHierarchy;
            if (!activeRow)
            {
                rows.Add(new(row.Pointer.ToInt64(), 0, 0, -1, 0, false, false, null, null));
                continue;
            }
            var owner = row.inspector;
            var rowNode = row.scriptNode;
            int index = row.index;
            if (!InteropObjectGuard.IsAlive(owner) || !InteropObjectGuard.IsAlive(rowNode)
                || owner.Pointer != inspector.Pointer || rowNode.Pointer != node.Pointer
                || index < 0 || index >= scriptCount)
                throw new InvalidOperationException("input-scene-row-structure-invalid");
            Script current = scripts[index];
            if (!InteropObjectGuard.IsAlive(current))
                throw new InvalidOperationException("input-scene-script-lifetime-unavailable");
            string? text = current.text;
            string? additional = current.additionalPrompt;
            if (text == null || additional == null)
                throw new InvalidOperationException("input-scene-script-read-unavailable");
            rows.Add(new(row.Pointer.ToInt64(), owner.Pointer.ToInt64(), rowNode.Pointer.ToInt64(),
                index, current.Pointer.ToInt64(), true, row.selected, text, additional));
            if (text == dialogueInput && additional == promptInput)
            {
                found = true;
                matchingRow = new(inspector, row, node, current, index > 0 ? scripts[index - 1] : null, index);
            }
        }
        if (!found) throw new InvalidOperationException("input-scene-match-unavailable");
        return new(context, dialogueInput, promptInput, rows.ToArray());
    }

    internal ApiResult<SceneSnapshot> ReadInternalSelection(InternalSceneSelection selection)
    {
        try
        {
            // FIX B source 2 lazy hook (stage-gated): only when neither a
            // rooted AAP path nor an auto-discovered active pair anchors the
            // project key may StudioCommon inputs be probed for adoption.
            if (!_files.HasCanonicalProjectKey
                && !ActiveProjectPairSource.HasActivePair)
            {
                LiveEditorProjectKeySource.TryAdoptFromStudioCommon(_files);
            }

            string projectKey = _files.Current.ProjectKey;
            object? guid = InteropMemberAccess.Get<object>(selection.Node, "guid");
            string nodeGuid = guid?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nodeGuid))
            {
                return ApiResult<SceneSnapshot>.Fail("Node.guid interop property returned an empty value.");
            }

            string currentText = InteropMemberAccess.Get<string>(selection.Current, "text") ?? string.Empty;
            string previousText = selection.Previous != null
                ? InteropMemberAccess.Get<string>(selection.Previous, "text") ?? string.Empty
                : string.Empty;
            string fingerprint = Fingerprint(projectKey, nodeGuid, selection.Index, currentText);

            _capabilities.Verified("Editor.Selection", "visible cache row/index/script resolved; execution requires selected-row confirmation");
            _capabilities.Verified("Editor.ScriptProperties", "Script.text and ScriptNode.scripts read through interop properties");
            _capabilities.Verified("Editor.NodeIdentity", "Node.guid read through interop property");

            return ApiResult<SceneSnapshot>.Ok(new SceneSnapshot(
                new SceneAddress(projectKey, nodeGuid, selection.Index, fingerprint),
                currentText,
                previousText,
                selection.Previous != null));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Editor.Selection", detail);
            return ApiResult<SceneSnapshot>.Fail(detail);
        }
    }

    internal bool TryGetInternalSelection(out InternalSceneSelection selection, out string error)
    {
        selection = default;
        error = string.Empty;

        ScriptNodeInspector? maybeInspector = ResolveInspector();
        if (!InteropObjectGuard.IsAlive(maybeInspector))
        {
            error = "No active ScriptNodeInspector.";
            return false;
        }

        ScriptNodeInspector inspector = maybeInspector!;
        var documents = Rukari.Lib.ModServices.Current?.GetService<Rukari.Lib.Editor.IEditorDocumentService>();
        if (documents is not { Success: true, Value: not null })
        {
            error = "The shared editor selection service is unavailable.";
            return false;
        }
        var shared = documents.Value.ReadSelection();
        if (!shared.Success || shared.Value == null)
        {
            error = shared.Error?.Message ?? "No synchronized editor line is selected.";
            return false;
        }
        // selectedScriptItem is a quarantined native getter. Use the existing visible cache,
        // then prove that the cache row belongs to this inspector/node and the shared snapshot.
        var rows = inspector.scriptNodeListItemsCache;
        if (!InteropObjectGuard.IsAlive(rows) || rows.Count > 10000 || inspector.rearrangeScheduled)
        {
            error = "The editor row cache is unavailable or being rearranged.";
            return false;
        }
        ScriptListItem? item = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var candidate = rows[i];
            if (!InteropObjectGuard.IsAlive(candidate) || candidate.gameObject == null
                || !candidate.gameObject.activeInHierarchy || !candidate.selected) continue;
            if (item != null) { error = "Multiple editor rows are selected."; return false; }
            item = candidate;
        }
        if (!InteropObjectGuard.IsAlive(item))
        {
            error = "No script row is selected.";
            return false;
        }

        ScriptListItem liveItem = item!;
        int index = InteropMemberAccess.Get<int>(liveItem, "index");
        ScriptNode? node = InteropMemberAccess.Get<ScriptNode>(liveItem, "scriptNode");
        var rowInspector = liveItem.inspector;
        var inspectorNode = inspector.scriptNode;
        if (!InteropObjectGuard.IsAlive(rowInspector) || !InteropObjectGuard.IsAlive(inspectorNode)
            || !InteropObjectGuard.IsAlive(node) || rowInspector.Pointer != inspector.Pointer
            || inspectorNode.Pointer != node!.Pointer)
        { error = "The selected cache row belongs to a different editor node."; return false; }

        if (!InteropObjectGuard.IsAlive(node))
        {
            error = "The selected row has no live ScriptNode property.";
            return false;
        }

        ScriptNode liveNode = node!;
        Il2CppSystem.Collections.Generic.List<Script>? scripts =
            InteropMemberAccess.Get<Il2CppSystem.Collections.Generic.List<Script>>(liveNode, "scripts");
        if (scripts == null)
        {
            error = "The selected ScriptNode.scripts interop property returned null.";
            return false;
        }

        if (index < 0 || index >= scripts.Count)
        {
            error = $"Selected index {index} is outside script count {scripts.Count}.";
            return false;
        }

        Script current = scripts[index];
        if (!InteropObjectGuard.IsAlive(current))
        {
            error = "The selected Script wrapper is not alive.";
            return false;
        }

        string context = $"{inspector.Pointer.ToInt64():X}:{liveNode.Pointer.ToInt64():X}:{current.Pointer.ToInt64():X}:{index}";
        if (shared.Value.ContextId != context
            || shared.Value.AdditionalPrompt != (current.additionalPrompt ?? string.Empty)
            || shared.Value.DialogueText != (current.text ?? string.Empty))
        { error = "The editor selection changed during resolution."; return false; }

        Script? previous = index > 0 ? scripts[index - 1] : null;
        selection = new InternalSceneSelection(inspector, liveItem, liveNode, current, previous, index);
        return true;
    }

    private ScriptNodeInspector? ResolveInspector()
    {
        // The editor recreates its inspector when projects or nodes change. A stale
        // IL2CPP wrapper can remain alive, so always resolve the active singleton.
        ScriptNodeInspector? inspector = InteropMemberAccess.GetStatic<ScriptNodeInspector>(
            typeof(ScriptNodeInspector),
            "instance");
        if (InteropObjectGuard.IsAlive(inspector))
        {
            _capabilities.Verified(
                "Editor.Inspector",
                "Active ScriptNodeInspector.instance resolved on demand without retaining its wrapper");
        }

        return inspector;
    }

    internal void MarkSelectedProof(bool success)
    {
        if (!VideoToolsConfig.FoundationProofVisibleMarker.Value
            || !TryGetInternalSelection(out InternalSceneSelection selection, out _))
        {
            return;
        }

        UILabel? label = InteropMemberAccess.Get<UILabel>(selection.Item, "nameLabel");
        if (!InteropObjectGuard.IsAlive(label))
        {
            return;
        }

        const string okPrefix = "[API OK] ";
        const string failPrefix = "[API FAIL] ";
        UILabel liveLabel = label!;
        string text = InteropMemberAccess.Get<string>(liveLabel, "text") ?? string.Empty;
        if (text.StartsWith(okPrefix, StringComparison.Ordinal))
        {
            text = text.Substring(okPrefix.Length);
        }
        else if (text.StartsWith(failPrefix, StringComparison.Ordinal))
        {
            text = text.Substring(failPrefix.Length);
        }

        InteropMemberAccess.Set(liveLabel, "text", (success ? okPrefix : failPrefix) + text);
        InteropMemberAccess.Set(
            liveLabel,
            "color",
            success ? new Color(0.35f, 1f, 0.45f, 1f) : new Color(1f, 0.35f, 0.35f, 1f));
    }

    /// <summary>
    /// The canonical live scene fingerprint (24-hex SHA256 prefix). Shared
    /// with the live graph composer so lease addresses and live-graph
    /// SceneKeys use one identical scheme.
    /// </summary>
    internal static string Fingerprint(string projectKey, string nodeGuid, int index, string text)
    {
        string source = $"{projectKey}\n{nodeGuid}\n{index}\n{text}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).Substring(0, 24);
    }

    internal readonly record struct InternalSceneSelection(
        ScriptNodeInspector Inspector,
        ScriptListItem Item,
        ScriptNode Node,
        Script Current,
        Script? Previous,
        int Index);
}
