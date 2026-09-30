extern alias unitycore;

using System;
using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
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

            _capabilities.Verified("Editor.Selection", "selectedScriptItem/index/script resolved");
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
        ScriptListItem? item = InteropMemberAccess.Get<ScriptListItem>(inspector, "selectedScriptItem");
        if (!InteropObjectGuard.IsAlive(item))
        {
            error = "No script row is selected.";
            return false;
        }

        ScriptListItem liveItem = item!;
        int index = InteropMemberAccess.Get<int>(liveItem, "index");
        ScriptNode? node = InteropMemberAccess.Get<ScriptNode>(liveItem, "scriptNode");
        if (!InteropObjectGuard.IsAlive(node))
        {
            node = InteropMemberAccess.Get<ScriptNode>(inspector, "scriptNode");
        }

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
