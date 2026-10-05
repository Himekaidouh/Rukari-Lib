using AzureArchive.Automation;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Interop;

/// <summary>
/// Reuses the typed getters already used by voice publication/import. Captures only path strings;
/// archive reads and layout validation run later, with no native object crossing a worker boundary.
/// </summary>
internal static class OfficialStoragePathBridge
{
    private static int _mainThreadId;
    private static long _nextCapture;
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);

    internal static bool CaptureOnMainThread(bool force)
    {
        if (!ActiveProjectPairSource.Enabled) return false;
        int thread = Environment.CurrentManagedThreadId;
        if (_mainThreadId == 0) _mainThreadId = thread;
        if (_mainThreadId != thread) return false;
        long now = Environment.TickCount64;
        if (!force && now < _nextCapture) return false;
        _nextCapture = now + 250;
        string project = string.Empty;
        string resource = string.Empty;
        bool unavailable = false;
        try
        {
            ScenarioResourceManager? manager = ScenarioResourceManager.Instance;
            if (!ReferenceEquals(manager, null) && !manager.WasCollected && manager.Pointer != IntPtr.Zero)
            {
                ResourceOverrideData? data = manager.LocalOverrideData;
                if (!ReferenceEquals(data, null) && !data.WasCollected && data.Pointer != IntPtr.Zero)
                    resource = data.basePath ?? string.Empty;
            }
            // Formal playback has its own live saves/name resource root. Do not let a stale,
            // retained authoring session choose the project or fail an otherwise valid capture.
            bool playingPackage = resource.Length != 0
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(
                    Path.TrimEndingDirectorySeparator(resource))), "saves", StringComparison.OrdinalIgnoreCase);
            if (!playingPackage)
            {
                AuthoringEditorSession? session = AuthoringEditorSession.Current;
                if (!ReferenceEquals(session, null) && !session.WasCollected && session.Pointer != IntPtr.Zero)
                    project = session.FilePath ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            unavailable = true;
            project = resource = string.Empty;
            string key = ex.GetType().Name;
            if (ReportedFailures.Add(key))
                Plugin.Logger.LogWarning("Official storage path capture unavailable; archive execution stays gated: "
                    + key + ": " + ex.Message);
        }
        if (!ActiveProjectPairSource.ObserveOfficialPaths(project, resource, unavailable)) return false;
        ContinueDialogueRuntime.InvalidateStorageContext("official-storage-path-changed");
        long revision = ActiveProjectPairSource.StorageRevision;
        Plugin.Host.SceneResolutionInternal.OnOfficialStoragePathsChanged(revision);
        PlayerCommandObservationRuntime.RequestIndexReload();
        PlayerCommandObservationLog.Append($"{DateTimeOffset.Now:O} official-storage-path changed=true; "
            + $"revision={ActiveProjectPairSource.StorageRevision}; projectPathPresent={project.Length != 0}; "
            + $"resourcePathPresent={resource.Length != 0}; captureUnavailable={unavailable}; oldPairExecutable=false");
        return true;
    }
}
