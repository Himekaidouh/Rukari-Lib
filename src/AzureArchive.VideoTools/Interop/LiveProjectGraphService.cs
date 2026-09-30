using System;
using System.Collections.Generic;
using System.Linq;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Studio.Scripts;
using Studio.Scripts.Nodes;

namespace AzureArchive.VideoTools.Interop;

/// <summary>
/// C feature Phase 2: walks the editor's live node graph on the Unity main
/// thread and composes a Core <see cref="ProjectSnapshot"/> that is
/// structurally identical to a disk-read AAP graph.
///
/// Red lines: read-only (no official object is written); main-thread only
/// (same gate pattern as <see cref="LiveEditorProjectKeySource"/>); fail-open
/// (every failure returns false and the caller keeps using the disk chain
/// source); only copied leaf values are retained — no IL2CPP wrapper ever
/// outlives this call.
/// </summary>
internal static class LiveProjectGraphService
{
    /// <summary>Debounce applied by consumers between rebuilds.</summary>
    public const int RebuildDebounceMilliseconds = 500;

    private const int MaximumNodes = 4096;

    private static readonly object Gate = new();
    private static int _mainThreadId = -1;
    private static int _pendingInvalidation = 1; // build once at startup
    private static long _lastUnavailableLogTicks;
    private static bool _edgeInversionLogged;

    /// <summary>
    /// Marks the cached live graph stale. Safe from any thread; the next
    /// debounced main-thread refresh rebuilds. Cheap on purpose: directive
    /// compiles fire per keystroke.
    /// </summary>
    public static void Invalidate(string reason)
    {
        System.Threading.Interlocked.Exchange(ref _pendingInvalidation, 1);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Plugin.Logger.LogDebug($"live-graph invalidate reason={reason}");
        }
    }

    /// <summary>Consumes the invalidation flag (main-thread refresh path).</summary>
    public static bool ConsumeInvalidation() =>
        System.Threading.Interlocked.Exchange(ref _pendingInvalidation, 0) == 1;

    /// <summary>
    /// Builds one snapshot of the current editor graph. Returns false when
    /// the editor is closed, the graph is unreadable, or any red-line guard
    /// trips — callers must fall back to the disk graph in that case.
    /// </summary>
    public static bool TryBuildSnapshot(out ProjectSnapshot snapshot, out string error)
    {
        snapshot = null!;
        error = string.Empty;
        if (!EnsureMainThreadGate())
        {
            error = "live graph build rejected off the Unity main thread.";
            return false;
        }

        try
        {
            return WalkAndCompose(out snapshot, out error);
        }
        catch (Exception ex)
        {
            error = $"live graph walk failed: {ex.GetType().Name}: {ex.Message}";
            LogUnavailableThrottled(error);
            return false;
        }
    }

    private static bool WalkAndCompose(out ProjectSnapshot snapshot, out string error)
    {
        snapshot = null!;
        error = string.Empty;

        string projectKey = Plugin.Api.Files.Current.ProjectKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(projectKey))
        {
            error = "no canonical project key (pair not bound yet).";
            return false;
        }

        object? studioCommon = InteropMemberAccess.GetStatic<object>(
            typeof(StudioCommon),
            "instance");
        if (!IsAlive(studioCommon))
        {
            error = "StudioCommon.instance is not alive (editor scene closed?).";
            return false;
        }

        object? entryNode = InteropMemberAccess.Get<object>(studioCommon!, "entryNode");
        if (!IsAlive(entryNode))
        {
            error = "StudioCommon.entryNode is not alive.";
            return false;
        }

        List<object> outgoingRoot = ReadConnectionTargets(entryNode!, preferFromSide: true);
        List<object> incomingRoot = ReadConnectionTargets(entryNode!, preferFromSide: false);

        // Edge-direction self-test: the entry node has only outgoing edges.
        // If the observed shape contradicts the naming assumption, invert for
        // this build instead of silently walking the graph backwards.
        bool fromIsOutgoing = outgoingRoot.Count > 0 || incomingRoot.Count == 0;
        if (outgoingRoot.Count == 0 && incomingRoot.Count > 0)
        {
            fromIsOutgoing = false;
            if (!_edgeInversionLogged)
            {
                _edgeInversionLogged = true;
                Plugin.Logger.LogWarning(
                    "live-graph edge direction self-test inverted: entry has incoming-only connections; "
                    + "connectionsTo will be treated as outgoing for this session's builds.");
            }
        }

        var diagnostics = new WalkDiagnostics();
        var nodesByGuid = new Dictionary<string, LiveGraphNodeData>(StringComparer.Ordinal);
        object rootNode = entryNode!;
        var pending = new Queue<object>();
        pending.Enqueue(rootNode);
        var enqueued = new HashSet<object>();

        while (pending.Count > 0)
        {
            object node = pending.Dequeue();
            if (!IsAlive(node) || !enqueued.Add(node))
            {
                continue;
            }

            if (nodesByGuid.Count >= MaximumNodes)
            {
                Plugin.Logger.LogWarning(
                    $"live-graph node cap reached ({MaximumNodes}); remaining subgraph ignored.");
                break;
            }

            string guid = ReadGuid(node, diagnostics);
            if (string.IsNullOrEmpty(guid))
            {
                continue;
            }

            if (nodesByGuid.ContainsKey(guid))
            {
                continue;
            }

            StoryNodeKind kind = DetectKind(node);
            List<string> connectionGuids = new();
            List<object> targets = fromIsOutgoing
                ? ReadConnectionTargets(node, preferFromSide: true)
                : ReadConnectionTargets(node, preferFromSide: false);
            foreach (object target in targets)
            {
                string targetGuid = ReadGuid(target, diagnostics);
                if (!string.IsNullOrEmpty(targetGuid))
                {
                    connectionGuids.Add(targetGuid);
                }

                // Enqueue even when the guid is unreadable: the per-node
                // diagnostics then record the wrapper type instead of the
                // subgraph silently disappearing.
                if (enqueued.Add(target))
                {
                    pending.Enqueue(target);
                }
            }

            bool deletionMarked = false;
            try
            {
                deletionMarked = InteropMemberAccess.Get<bool>(node, "deletionMarked");
            }
            catch
            {
                // Property missing on exotic wrappers: keep the node.
            }

            List<LiveGraphSceneData> scenes = kind == StoryNodeKind.Script && !deletionMarked
                ? ReadScenes(node, guid, projectKey)
                : new List<LiveGraphSceneData>();

            nodesByGuid[guid] = new LiveGraphNodeData(
                nodesByGuid.Count,
                kind,
                guid,
                ReadName(node, kind),
                connectionGuids.AsReadOnly(),
                scenes.AsReadOnly());
        }

        List<LiveGraphNodeData> ordered = nodesByGuid.Values
            .OrderBy(node => node.SourceIndex)
            .ToList();

        // Guard against the silent-empty-graph failure mode: a walk that
        // discovers no script scenes must FAIL (disk fallback) instead of
        // publishing a READY source with zero directives that shadows the
        // healthy disk graph.
        int totalScenes = ordered.Sum(node => node.Scenes.Count);
        LogWalkSummaryThrottled(
            $"nodes={ordered.Count} "
            + $"kinds={string.Join("+", ordered.Select(node => node.Kind.ToString()))} "
            + $"scenes={totalScenes} "
            + $"guids={string.Join(";", ordered.Take(4).Select(node => Short16Guid(node.NodeGuid)))}");
        if (!ordered.Any(node => node.Kind == StoryNodeKind.Script) || totalScenes == 0)
        {
            error = "live graph walk found no script scenes "
                + $"(nodes={ordered.Count}); falling back to the disk graph.";
            LogUnavailableThrottled(error);
            return false;
        }

        Result<ProjectSnapshot> composed =
            LiveProjectGraphComposer.Compose(projectKey, ordered);
        if (!composed.Success || composed.Value == null)
        {
            error = composed.Error;
            LogUnavailableThrottled(error);
            return false;
        }

        snapshot = composed.Value;
        return true;
    }

    private sealed class WalkDiagnostics
    {
        public bool GuidFailureLogged;
    }

    private static string ReadGuid(object node, WalkDiagnostics diagnostics)
    {
        string value = TryReadGuid(node);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        // A wrapper reached through NodeConnectionInfo.target can arrive as a
        // base/generic interop wrapper without the generated property
        // surface; re-read through an explicit Node cast before giving up.
        try
        {
            if (node is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase il2cppObject)
            {
                Node? cast = il2cppObject.TryCast<Node>();
                if (!ReferenceEquals(cast, null))
                {
                    value = TryReadGuid(cast!);
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
            // fall through to the diagnostic
        }

        if (!diagnostics.GuidFailureLogged)
        {
            diagnostics.GuidFailureLogged = true;
            Plugin.Logger.LogWarning(
                "live-graph node guid unreadable; runtimeType="
                + node.GetType().FullName
                + "; toString=" + SafeShort(node.ToString()));
        }

        return string.Empty;
    }

    private static string TryReadGuid(object node)
    {
        try
        {
            object? raw = InteropMemberAccess.Get<object>(node, "guid");
            return (raw?.ToString() ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeShort(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string single = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return single.Length <= 80 ? single : single[..80];
    }

    private static string Short16Guid(string value) =>
        value.Length <= 16 ? value : value[..16];

    private static long _lastWalkSummaryLogTicks = long.MinValue / 2;

    private static void LogWalkSummaryThrottled(string summary)
    {
        long now = Environment.TickCount64;
        if (now - _lastWalkSummaryLogTicks < 10_000)
        {
            return;
        }

        _lastWalkSummaryLogTicks = now;
        Plugin.Logger.LogInfo($"live-graph walk summary: {summary}");
    }

    private static List<LiveGraphSceneData> ReadScenes(
        object scriptNode,
        string nodeGuid,
        string projectKey)
    {
        var scripts = InteropMemberAccess.Get<Il2CppSystem.Collections.Generic.List<Script>>(
            scriptNode,
            "scripts");
        var scenes = new List<LiveGraphSceneData>();
        if (scripts == null)
        {
            Plugin.Logger.LogWarning(
                $"live-graph ScriptNode {nodeGuid} returned a null scripts collection.");
            return scenes;
        }

        int count = scripts.Count;
        for (int index = 0; index < count; index++)
        {
            scenes.Add(ReadScene(scripts[index], nodeGuid, index, projectKey));
        }

        return scenes;
    }

    private static LiveGraphSceneData ReadScene(
        Script? script,
        string nodeGuid,
        int index,
        string projectKey)
    {
        // Index alignment is load-bearing: lease addresses address scenes by
        // index, so a dead wrapper still emits one empty record.
        if (!IsAlive(script))
        {
            return new LiveGraphSceneData(
                index,
                PlaceholderFingerprint(nodeGuid, index),
                LiveProjectGraphComposer.LiveSourceType,
                DialogueText: string.Empty,
                IsDialogue: false,
                PopupFileName: string.Empty,
                BackgroundEffect: 0u,
                BackgroundName: 0u,
                AdditionalPrompt: string.Empty,
                PlaceText: string.Empty,
                BackgroundFriendlyName: string.Empty,
                SoundReference: string.Empty,
                VoiceReference: string.Empty,
                Transition: 0u,
                BgmId: 0L,
                SelectionGroup: 0L,
                SpeakerSlot: 0,
                HighlightedSlots: Array.Empty<int>(),
                Characters: Array.Empty<LiveGraphCharacterData>());
        }

        string text = ReadString(script!, "text");
        string additionalPrompt = ReadString(script!, "additionalPrompt");

        var characters = new List<LiveGraphCharacterData>();
        try
        {
            Il2CppReferenceArray<Script.CharacterRecord>? records =
                InteropMemberAccess.Get<Il2CppReferenceArray<Script.CharacterRecord>>(
                    script!,
                    "characters");
            if (records != null)
            {
                // Slot convention mirrors the disk reader: array position IS
                // the physical slot; position 0 and anything above 5 are
                // ignored exactly like AapProjectReader.ParseCharacters.
                // The device probe (NOTES_LiveGraph_Interop_Evidence §7)
                // validates the head-placeholder shape on hardware.
                for (int slot = 1; slot < records.Length && slot <= 5; slot++)
                {
                    Script.CharacterRecord? record = records[slot];
                    if (!IsAlive(record))
                    {
                        continue;
                    }

                    characters.Add(new LiveGraphCharacterData(
                        slot,
                        ReadString(record!, "name"),
                        ReadString(record!, "faceId"),
                        ReadInt32(record!, "startingPos"),
                        ReadInt32(record!, "endingPos"),
                        (int)InteropMemberAccess.Get<EmoticonType>(record!, "emoticon"),
                        (int)InteropMemberAccess.Get<CharacterAction>(record!, "action"),
                        (int)InteropMemberAccess.Get<CharacterEffect>(record!, "effect"),
                        (int)InteropMemberAccess.Get<Script.CharacterRecord.AppearType>(
                            record!,
                            "appear"),
                        (int)InteropMemberAccess.Get<Script.CharacterRecord.ShapeOverrideType>(
                            record!,
                            "shapeOverride")));
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(
                $"live-graph characters read failed for {nodeGuid}:{index}: "
                + $"{ex.GetType().Name}: {ex.Message}");
        }

        List<int> highlighted = new();
        try
        {
            // The interop facade referenced at compile time does not expose
            // Il2CppSystem HashSet<T>; the runtime wrapper still implements
            // the managed IEnumerable contract, so enumerate defensively.
            object? slots = InteropMemberAccess.Get<object>(
                script!,
                "highlightedSlotNums");
            if (slots is System.Collections.IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    try
                    {
                        highlighted.Add(Convert.ToInt32(item));
                    }
                    catch
                    {
                        // Skip malformed entries; the field is cosmetic.
                    }
                }
            }
        }
        catch
        {
            // Cosmetic field: ignore enumeration failures.
        }

        return new LiveGraphSceneData(
            index,
            EditorSceneService.Fingerprint(projectKey, nodeGuid, index, text),
            LiveProjectGraphComposer.LiveSourceType,
            text,
            ReadBoolean(script!, "isDialogScript", defaultValue: true),
            ReadString(script!, "popup"),
            ReadUInt32(script!, "bgEffect"),
            ReadUInt32(script!, "bgName"),
            additionalPrompt,
            ReadString(script!, "placeText"),
            ReadString(script!, "bgFriendlyName"),
            ReadString(script!, "sound"),
            ReadString(script!, "voice"),
            ReadUInt32(script!, "transition"),
            ReadInt64(script!, "bgmId"),
            ReadInt64(script!, "selectionGroup"),
            ReadInt32(script!, "speakerSlotNum"),
            highlighted,
            characters);
    }

    private static List<object> ReadConnectionTargets(object node, bool preferFromSide)
    {
        var targets = new List<object>();
        string propertyName = preferFromSide ? "connectionsFrom" : "connectionsTo";
        try
        {
            var connections =
                InteropMemberAccess.Get<
                    Il2CppSystem.Collections.Generic.List<Node.NodeConnectionInfo>>(
                    node,
                    propertyName);
            if (connections == null)
            {
                return targets;
            }

            int count = connections.Count;
            for (int index = 0; index < count; index++)
            {
                Node.NodeConnectionInfo? info = connections[index];
                if (info == null)
                {
                    continue;
                }

                object? target = info.target;
                if (IsAlive(target))
                {
                    targets.Add(target!);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug(
                $"live-graph connection read failed ({propertyName}): "
                + $"{ex.GetType().Name}: {ex.Message}");
        }

        return targets;
    }

    private static StoryNodeKind DetectKind(object node)
    {
        Type type = node.GetType();
        if (HasProperty(type, "scripts"))
        {
            return StoryNodeKind.Script;
        }

        if (HasProperty(type, "selectionTexts"))
        {
            return StoryNodeKind.Selection;
        }

        if (HasProperty(type, "endText"))
        {
            return StoryNodeKind.Exit;
        }

        if (HasProperty(type, "header"))
        {
            return StoryNodeKind.Entry;
        }

        return StoryNodeKind.Unknown;
    }

    private static string ReadName(object node, StoryNodeKind kind)
    {
        if (kind == StoryNodeKind.Script)
        {
            return ReadString(node, "_nodeName");
        }

        string title = ReadString(node, "title");
        return title.Length != 0 ? title : ReadString(node, "endText");
    }

    private static string ReadString(object target, string property)
    {
        try
        {
            return InteropMemberAccess.Get<string>(target, property) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadInt32(object target, string property)
    {
        try
        {
            return InteropMemberAccess.Get<int>(target, property);
        }
        catch
        {
            return 0;
        }
    }

    private static long ReadInt64(object target, string property)
    {
        try
        {
            return InteropMemberAccess.Get<long>(target, property);
        }
        catch
        {
            return 0L;
        }
    }

    private static uint ReadUInt32(object target, string property)
    {
        try
        {
            return InteropMemberAccess.Get<uint>(target, property);
        }
        catch
        {
            return 0u;
        }
    }

    private static bool ReadBoolean(object target, string property, bool defaultValue)
    {
        try
        {
            bool? value = InteropMemberAccess.Get<bool>(target, property);
            return value ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool HasProperty(Type type, string name)
    {
        try
        {
            return type.GetProperty(
                name,
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance) != null;
        }
        catch
        {
            return false;
        }
    }

    private static string PlaceholderFingerprint(string nodeGuid, int index) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"live-unreadable\n{nodeGuid}\n{index}")))
            .ToLowerInvariant();

    private static bool IsAlive(object? value) =>
        !ReferenceEquals(value, null) && InteropObjectGuard.IsAlive(value);

    private static bool EnsureMainThreadGate()
    {
        lock (Gate)
        {
            if (_mainThreadId == -1)
            {
                _mainThreadId = Environment.CurrentManagedThreadId;
                return true;
            }

            return _mainThreadId == Environment.CurrentManagedThreadId;
        }
    }

    private static void LogUnavailableThrottled(string error)
    {
        long now = Environment.TickCount64;
        if (now - _lastUnavailableLogTicks < 10_000)
        {
            return;
        }

        _lastUnavailableLogTicks = now;
        Plugin.Logger.LogInfo($"live graph unavailable: {error}");
    }
}
