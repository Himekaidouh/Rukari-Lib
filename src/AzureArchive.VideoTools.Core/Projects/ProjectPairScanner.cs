using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Projects;

public sealed record DiscoveredProjectPair(
    string Name,
    string AapPath,
    string AasPath,
    bool PlaybackPresent);

/// <summary>
/// Scans a game data root for same-name AAP/AAS project pairs. Read-only and
/// deterministic: pairs are ordered by file name so arbitration results never
/// depend on file system enumeration order.
/// </summary>
public sealed class ProjectPairScanner
{
    public Result<IReadOnlyList<DiscoveredProjectPair>> Scan(string dataRoot) =>
        Scan(dataRoot, DataRootStampSet.Capture(dataRoot));

    /// <summary>
    /// Scans using a caller-supplied <see cref="DataRootStampSet"/> for playback
    /// presence. The caller is responsible for capturing the stamps as part of
    /// the same operation, so that presence is judged against the same instant
    /// as the rest of its work; a path the set does not describe is still
    /// queried directly.
    /// </summary>
    public Result<IReadOnlyList<DiscoveredProjectPair>> Scan(
        string dataRoot,
        DataRootStampSet stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return Result<IReadOnlyList<DiscoveredProjectPair>>.Fail(
                "The auto-discovery data root is empty.");
        }

        string projectsDirectory;
        string savesDirectory;
        try
        {
            projectsDirectory = Path.Combine(dataRoot.Trim(), "projects");
            savesDirectory = Path.Combine(dataRoot.Trim(), "saves");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DiscoveredProjectPair>>.Fail(
                $"The auto-discovery data root is invalid: {ex.Message}");
        }

        if (!Directory.Exists(projectsDirectory))
        {
            return Result<IReadOnlyList<DiscoveredProjectPair>>.Fail(
                $"No projects directory exists under the data root: {projectsDirectory}");
        }

        bool savesExist = Directory.Exists(savesDirectory);
        var pairs = new List<DiscoveredProjectPair>();
        try
        {
            foreach (string aapPath in Directory.EnumerateFiles(
                projectsDirectory,
                "*.aap"))
            {
                string name = Path.GetFileNameWithoutExtension(aapPath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string aasPath = Path.Combine(savesDirectory, $"{name}.aas");
                pairs.Add(new DiscoveredProjectPair(
                    name,
                    Path.GetFullPath(aapPath),
                    Path.GetFullPath(aasPath),
                    savesExist
                        && (stamps.TryGet(aasPath, out _) || File.Exists(aasPath))));
            }
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DiscoveredProjectPair>>.Fail(
                $"Scanning the projects directory failed: {ex.Message}");
        }

        pairs.Sort(
            (left, right) => string.Compare(
                left.Name,
                right.Name,
                StringComparison.OrdinalIgnoreCase));
        return Result<IReadOnlyList<DiscoveredProjectPair>>.Ok(
            Array.AsReadOnly(pairs.ToArray()));
    }
}
