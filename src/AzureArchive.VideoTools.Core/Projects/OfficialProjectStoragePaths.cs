using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Projects;

/// <summary>Managed strings copied from already-used official getters on the game thread.</summary>
public sealed record OfficialProjectPathSnapshot(
    long Revision,
    string ProjectFilePath,
    string ResourceDirectory,
    bool CaptureUnavailable);

/// <summary>A path change invalidates work started for the previous storage/project context.</summary>
public sealed class OfficialProjectPathTracker
{
    private readonly object _gate = new();
    private OfficialProjectPathSnapshot _current = new(0, string.Empty, string.Empty, false);

    public OfficialProjectPathSnapshot Current { get { lock (_gate) return _current; } }
    public bool IsCurrent(long revision) { lock (_gate) return _current.Revision == revision; }
    public bool CommitIfCurrent(long revision, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_gate)
        {
            if (_current.Revision != revision) return false;
            publish();
            return true;
        }
    }

    public bool Observe(string projectFilePath, string resourceDirectory, bool captureUnavailable)
    {
        string project = NormalizeObservation(projectFilePath);
        string resource = NormalizeObservation(resourceDirectory);
        lock (_gate)
        {
            if (Same(_current.ProjectFilePath, project)
                && Same(_current.ResourceDirectory, resource)
                && _current.CaptureUnavailable == captureUnavailable) return false;
            var next = new OfficialProjectPathSnapshot(_current.Revision, project, resource, captureUnavailable);
            if (!_current.CaptureUnavailable && !captureUnavailable
                && OfficialProjectStoragePaths.DescribeSamePair(_current, next))
            {
                // Switching from projects/name to saves/name for this same project is a
                // playback mode change, not a new archive identity. Keep the verified disk index.
                _current = next;
                return false;
            }
            _current = new(_current.Revision + 1, project, resource, captureUnavailable);
            return true;
        }
    }

    private static string NormalizeObservation(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            return Path.IsPathFullyQualified(value)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) : value.Trim();
        }
        catch { return value.Trim(); }
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public sealed record OfficialProjectStorageBinding(
    long Revision,
    string DataRoot,
    string ProjectFilePath,
    string PlaybackFilePath,
    string Source)
{
    /// <summary>The live path is an additional restriction, never a replacement for scene arbitration.</summary>
    public IReadOnlyList<DiscoveredProjectPair> Restrict(IReadOnlyList<DiscoveredProjectPair> pairs) =>
        ProjectFilePath.Length == 0 ? pairs : Array.AsReadOnly(pairs.Where(pair =>
            string.Equals(pair.AapPath, ProjectFilePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.AasPath, PlaybackFilePath, StringComparison.OrdinalIgnoreCase)).ToArray());
}

/// <summary>
/// Recognizes only AA's existing direct projects/*.aap and projects/name or saves/name
/// resource layouts. Does not search ancestors, remember other projects or write game files.
/// Native getters are deliberately absent; filesystem verification stays on the managed reader side.
/// </summary>
public static class OfficialProjectStoragePaths
{
    internal static bool DescribeSamePair(OfficialProjectPathSnapshot left, OfficialProjectPathSnapshot right) =>
        TryDescribe(left, out string leftRoot, out string leftName)
        && TryDescribe(right, out string rightRoot, out string rightName)
        && Same(leftRoot, rightRoot) && Same(leftName, rightName);

    private static bool TryDescribe(OfficialProjectPathSnapshot paths, out string root, out string name)
    {
        root = name = string.Empty;
        try
        {
            if (paths.ResourceDirectory.Length == 0)
                return paths.ProjectFilePath.Length != 0 && TryProjectLayout(paths.ProjectFilePath, out root, out name);
            if (!TryResourceLayout(paths.ResourceDirectory, out root, out name, out bool playback)) return false;
            return playback || paths.ProjectFilePath.Length == 0
                || (TryProjectLayout(paths.ProjectFilePath, out string fileRoot, out string fileName)
                    && Same(root, fileRoot) && Same(name, fileName));
        }
        catch { return false; }
    }

    public static Result<OfficialProjectStorageBinding> Resolve(
        OfficialProjectPathSnapshot paths, string dataRootOverride, string persistentDataPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.CaptureUnavailable)
            return Fail("official-path-capture-unavailable");
        try
        {
            string root = string.Empty;
            string name = string.Empty;
            string source = string.Empty;
            bool playback = false;
            if (paths.ResourceDirectory.Length != 0)
            {
                if (!TryResourceLayout(paths.ResourceDirectory, out root, out name, out playback))
                    return Fail("official-resource-layout-unrecognized");
                source = playback ? "official-playback-resource-path" : "official-project-resource-path";
                // A retained editor session must not override the package currently being played.
                if (!playback && paths.ProjectFilePath.Length != 0)
                {
                    bool recognized = TryProjectLayout(paths.ProjectFilePath,
                        out string projectRoot, out string projectName);
                    if (!recognized || !Same(root, projectRoot) || !Same(name, projectName))
                        return Fail("official-project-resource-path-mismatch"
                            + $"; revision={paths.Revision}; projectLayoutRecognized={recognized}"
                            + $"; rootMatches={recognized && Same(root, projectRoot)}"
                            + $"; nameMatches={recognized && Same(name, projectName)}"
                            + $"; projectFilePath='{DiagnosticPath(paths.ProjectFilePath)}'"
                            + $"; resourceDirectory='{DiagnosticPath(paths.ResourceDirectory)}'");
                }
            }
            else if (paths.ProjectFilePath.Length != 0)
            {
                if (!TryProjectLayout(paths.ProjectFilePath, out root, out name))
                    return Fail("official-project-file-layout-unrecognized");
                source = "official-authoring-file-path";
            }

            if (!string.IsNullOrWhiteSpace(dataRootOverride))
            {
                if (!TryAbsolute(dataRootOverride, out string configuredRoot))
                    return Fail("configured-data-root-not-absolute");
                if (root.Length != 0 && !Same(root, configuredRoot))
                    return Fail("configured-data-root-conflicts-with-official-path");
                root = configuredRoot;
                if (source.Length == 0) source = "explicit-data-root";
            }
            if (root.Length == 0)
            {
                // Legacy default remains available only when official getters supplied no path.
                // A malformed/missing custom path must never fall back to an unrelated default root.
                if (!TryAbsolute(persistentDataPath, out string persistent))
                    return Fail("default-data-root-unavailable");
                root = Path.Combine(persistent, "data");
                source = "default-persistent-data-path";
            }
            if (!Directory.Exists(root) || !Directory.Exists(Path.Combine(root, "projects")))
                return Fail("data-root-projects-directory-unavailable");
            string aap = name.Length == 0 ? string.Empty : Path.Combine(root, "projects", name + ".aap");
            string aas = name.Length == 0 ? string.Empty : Path.Combine(root, "saves", name + ".aas");
            if (paths.ResourceDirectory.Length != 0 && !Directory.Exists(paths.ResourceDirectory))
                return Fail("official-resource-directory-unavailable");
            if (name.Length != 0 && !File.Exists(aap))
                return Fail("official-project-file-unavailable");
            if (playback && !File.Exists(aas))
                return Fail("official-playback-file-unavailable");
            return Result<OfficialProjectStorageBinding>.Ok(new(paths.Revision, root, aap, aas, source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return Fail("official-path-invalid:" + ex.GetType().Name);
        }
    }

    private static bool TryProjectLayout(string value, out string root, out string name)
    {
        root = name = string.Empty;
        if (!TryAbsolute(value, out string full) || !Same(Path.GetExtension(full), ".aap")) return false;
        string? branch = Path.GetDirectoryName(full);
        if (branch == null || !Same(Path.GetFileName(branch), "projects")) return false;
        string? candidateRoot = Path.GetDirectoryName(branch);
        name = Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrWhiteSpace(candidateRoot) || string.IsNullOrWhiteSpace(name)) return false;
        root = candidateRoot;
        return true;
    }

    private static bool TryResourceLayout(string value, out string root, out string name, out bool playback)
    {
        root = name = string.Empty;
        playback = false;
        if (!TryAbsolute(value, out string full)) return false;
        string? branch = Path.GetDirectoryName(full);
        if (branch == null) return false;
        string branchName = Path.GetFileName(branch);
        playback = Same(branchName, "saves");
        if (!playback && !Same(branchName, "projects")) return false;
        string? candidateRoot = Path.GetDirectoryName(branch);
        name = Path.GetFileName(full);
        if (string.IsNullOrWhiteSpace(candidateRoot) || string.IsNullOrWhiteSpace(name)) return false;
        root = candidateRoot;
        return true;
    }

    private static bool TryAbsolute(string value, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        return true;
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // Keep path evidence on one bounded log line. It is diagnostic only and never
    // relaxes the requirement that both official getters identify the same project.
    private static string DiagnosticPath(string value)
    {
        const int maximumCharacters = 512;
        string singleLine = value.Replace("\r", "\\r").Replace("\n", "\\n")
            .Replace("\t", "\\t").Replace("'", "\\'");
        return singleLine.Length <= maximumCharacters ? singleLine : singleLine[..maximumCharacters] + "...";
    }

    private static Result<OfficialProjectStorageBinding> Fail(string reason) =>
        Result<OfficialProjectStorageBinding>.Fail("reason=" + reason);
}
