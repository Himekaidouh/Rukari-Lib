using System;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Runtime;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Interop;

/// <summary>
/// FIX B source 2 (STAGE-GATED, default OFF): adopt a session live project key
/// composed from StudioCommon's <c>projectName</c>/<c>savedProjectName</c>/
/// <c>entryNode.guid</c>. Unity main thread only; every getter result is
/// null/liveness checked; nothing but copied strings, lengths, and the
/// composed key is retained. Two consecutive main-thread attempts must observe
/// a byte-equal triple before anything is adopted (first sight only records a
/// provisional snapshot and stays fail-closed).
/// </summary>
internal static class LiveEditorProjectKeySource
{
    private static readonly object Gate = new();
    private static volatile bool _enabled;
    private static int _mainThreadId = -1;
    private static bool _hasProvisional;
    private static string _provisionalProjectName = string.Empty;
    private static string _provisionalSavedProjectName = string.Empty;
    private static string _provisionalEntryNodeGuid = string.Empty;
    private static string _provisionalKey = string.Empty;
    private static string _adoptedStableKey = string.Empty;

    /// <summary>
    /// Staging switch wired from config by ConfiguredSceneResolutionService;
    /// default false keeps behavior identical to the fail-closed sentinel.
    /// </summary>
    public static bool Enabled => _enabled;

    public static void Configure(bool enabled)
    {
        _enabled = enabled;
        Plugin.Logger.LogInfo(
            $"Live project key source configured: enabled={enabled}; "
            + "adoption=two-consecutive-main-thread-probes; precedence=rooted-aap-path-wins.");
    }

    /// <summary>
    /// Attempts one main-thread probe/adoption cycle. Returns true only when a
    /// triple was adopted AND it rotated the effective project key.
    /// </summary>
    public static bool TryAdoptFromStudioCommon(ScenarioFileService files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!_enabled)
        {
            return false;
        }

        if (!EnsureMainThreadGate())
        {
            return false;
        }

        try
        {
            return ProbeAndAdopt(files);
        }
        catch (Exception ex)
        {
            Plugin.Host.CapabilitiesInternal.Degraded(
                "Files.LiveProjectKey",
                $"studio-common probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool EnsureMainThreadGate()
    {
        lock (Gate)
        {
            if (_mainThreadId == -1)
            {
                _mainThreadId = Environment.CurrentManagedThreadId;
                return true;
            }

            if (_mainThreadId == Environment.CurrentManagedThreadId)
            {
                return true;
            }
        }

        Plugin.Host.CapabilitiesInternal.Degraded(
            "Files.LiveProjectKey",
            $"studio-common probe rejected off the Unity main thread "
            + $"(captured={_mainThreadId}; current={Environment.CurrentManagedThreadId}).");
        return false;
    }

    private static bool ProbeAndAdopt(ScenarioFileService files)
    {
        object? instance = InteropMemberAccess.GetStatic<object>(typeof(StudioCommon), "instance");
        if (!IsLiveWrapper(instance))
        {
            return false;
        }

        string? projectName = InteropMemberAccess.Get<string>(instance!, "projectName");
        if (!HasText(projectName))
        {
            return false;
        }

        string? savedProjectName = InteropMemberAccess.Get<string>(instance!, "savedProjectName");
        if (!HasText(savedProjectName))
        {
            return false;
        }

        object? entryNode = InteropMemberAccess.Get<object>(instance!, "entryNode");
        if (!IsLiveWrapper(entryNode))
        {
            return false;
        }

        // Il2CppSystem.Guid is read defensively as object and formatted with
        // its default "D" ToString(); never cast across the Guid types.
        object? rawGuid = InteropMemberAccess.Get<object>(entryNode!, "guid");
        string entryNodeGuid = ReferenceEquals(rawGuid, null) ? string.Empty : rawGuid.ToString() ?? string.Empty;
        if (!LiveProjectKeyComposer.IsValidInput(projectName, savedProjectName, entryNodeGuid))
        {
            return false;
        }

        string projectNameValue = projectName!.Trim();
        string savedProjectNameValue = savedProjectName!.Trim();
        string entryNodeGuidValue = entryNodeGuid.Trim();
        string key = LiveProjectKeyComposer.Compose(
            projectNameValue,
            savedProjectNameValue,
            entryNodeGuidValue);

        lock (Gate)
        {
            bool matchesProvisional = _hasProvisional
                && string.Equals(_provisionalKey, key, StringComparison.Ordinal)
                && string.Equals(_provisionalProjectName, projectNameValue, StringComparison.Ordinal)
                && string.Equals(_provisionalSavedProjectName, savedProjectNameValue, StringComparison.Ordinal)
                && string.Equals(_provisionalEntryNodeGuid, entryNodeGuidValue, StringComparison.Ordinal);
            if (!matchesProvisional)
            {
                // First valid sighting (or a changed triple): record only,
                // publish nothing, stay fail-closed.
                _hasProvisional = true;
                _provisionalProjectName = projectNameValue;
                _provisionalSavedProjectName = savedProjectNameValue;
                _provisionalEntryNodeGuid = entryNodeGuidValue;
                _provisionalKey = key;
                LogAdopt("provisional", key, projectNameValue, savedProjectNameValue, entryNodeGuidValue);
                return false;
            }

            // Already adopted and nothing changed: silent no-op, no churn.
            if (string.Equals(_adoptedStableKey, key, StringComparison.Ordinal))
            {
                return false;
            }

            files.AdoptLiveProjectKey(key, out bool rotated);
            _adoptedStableKey = key;
            LogAdopt("confirmed", key, projectNameValue, savedProjectNameValue, entryNodeGuidValue);
            if (rotated)
            {
                EmbeddedEditorPreviewLeaseCache.OnProjectKeyRotated(
                    "studio-common-inputs-changed",
                    ShortKey(files.EffectiveProjectKey));
                LiveProjectGraphService.Invalidate("project-key-rotated");
                return true;
            }

            return false;
        }
    }

    private static void LogAdopt(
        string stage,
        string key,
        string projectName,
        string savedProjectName,
        string entryNodeGuid) =>
        Plugin.Logger.LogInfo(
            $"editor-project-key event=ADOPT stage={stage}; key16={ShortKey(key)}; "
            + $"projectNameLen={projectName.Length}; savedProjectNameLen={savedProjectName.Length}; "
            + $"entryGuidLen={entryNodeGuid.Length}");

    private static bool IsLiveWrapper(object? value) =>
        !ReferenceEquals(value, null) && InteropObjectGuard.IsAlive(value);

    private static string ShortKey(string key) =>
        key.Length <= 16 ? key : key[..16];

    private static bool HasText(string? value) =>
        !string.IsNullOrWhiteSpace(value);
}
