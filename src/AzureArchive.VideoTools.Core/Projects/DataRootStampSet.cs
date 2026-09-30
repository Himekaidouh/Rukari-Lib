namespace AzureArchive.VideoTools.Core.Projects;

/// <summary>Length and last-write time of one file.</summary>
public readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// The length and last-write time of every file under a data root's
/// <c>projects/</c> and <c>saves/</c> directories, captured by enumerating
/// those two directories with <see cref="DirectoryInfo.EnumerateFiles()"/>.
///
/// A directory enumeration returns each entry's name, length and write time in
/// one pass, so a whole data root can be stamped for the cost of two directory
/// reads. Asking the file system about each file individually instead costs a
/// separate call per file, which measured roughly fifty times more expensive
/// per file on the machine this was profiled on. Scanners and caches therefore
/// take their metadata from one of these sets, captured at the start of the
/// operation that needs it, rather than stat-ing files one at a time.
///
/// The set is a snapshot of a moment, not a cache: callers capture a new one
/// whenever they need current metadata, and a path that is absent from it must
/// be treated as unknown rather than as missing, so callers fall back to
/// asking the file system directly.
/// </summary>
public sealed class DataRootStampSet
{
    private static readonly string[] StampedDirectories = { "projects", "saves" };

    private readonly Dictionary<string, FileStamp> _stamps;

    private DataRootStampSet(Dictionary<string, FileStamp> stamps)
    {
        _stamps = stamps;
    }

    /// <summary>A set that knows about nothing; every lookup falls back.</summary>
    public static DataRootStampSet Empty { get; } = new(
        new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Number of files this set describes.</summary>
    public int Count => _stamps.Count;

    /// <summary>
    /// Enumerates the data root's project and playback directories once. An
    /// unreadable or missing directory simply contributes no stamps, and a
    /// failure never throws: the caller keeps working by falling back to
    /// per-file queries.
    /// </summary>
    public static DataRootStampSet Capture(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return Empty;
        }

        string root;
        try
        {
            root = Path.GetFullPath(dataRoot.Trim());
        }
        catch (Exception)
        {
            return Empty;
        }

        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in StampedDirectories)
        {
            try
            {
                var directory = new DirectoryInfo(Path.Combine(root, name));
                if (!directory.Exists)
                {
                    continue;
                }

                foreach (FileInfo file in directory.EnumerateFiles())
                {
                    stamps[file.FullName] = new FileStamp(
                        file.Length,
                        file.LastWriteTimeUtc);
                }
            }
            catch (Exception)
            {
                // Stamps are an optimization: a directory that cannot be
                // listed contributes nothing and callers fall back per file.
            }
        }

        return new DataRootStampSet(stamps);
    }

    /// <summary>
    /// Looks up one path. Returns false when this set does not describe it,
    /// which means "unknown", not "missing".
    /// </summary>
    public bool TryGet(string path, out FileStamp stamp) =>
        _stamps.TryGetValue(path, out stamp);
}
