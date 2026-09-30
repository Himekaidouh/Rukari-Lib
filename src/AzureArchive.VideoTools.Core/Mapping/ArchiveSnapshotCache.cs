using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Mapping;

/// <summary>
/// Caches parsed AAP/AAS snapshots keyed by absolute path and validates each
/// hit against the file's last-write time and length, so editing or
/// re-exporting an archive invalidates exactly that entry. Callers that already
/// hold a <see cref="DataRootStampSet"/> can hand it in so one directory
/// enumeration validates a whole sweep instead of one file system call per
/// file; everything else is unchanged. All methods are thread-safe; failures
/// are never cached.
/// </summary>
public sealed class ArchiveSnapshotCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ProjectEntry> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaybackEntry> _playbacks =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _projects.Count + _playbacks.Count;
            }
        }
    }

    public Result<ProjectSnapshot> GetOrReadProject(
        string path,
        Func<string, Result<ProjectSnapshot>> reader) =>
        GetOrReadProject(path, stamps: null, reader);

    /// <summary>
    /// Validates against <paramref name="stamps"/> when they describe the file.
    /// One directory enumeration then covers a whole sweep instead of one file
    /// system call per file. A path the stamps do not describe falls back to
    /// the per-file check, so a missing or unlistable directory degrades to the
    /// original behaviour rather than to a wrong answer.
    /// </summary>
    public Result<ProjectSnapshot> GetOrReadProject(
        string path,
        DataRootStampSet? stamps,
        Func<string, Result<ProjectSnapshot>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        CacheKey key;
        try
        {
            key = CacheKey.Create(path, stamps);
        }
        catch (Exception ex)
        {
            return Result<ProjectSnapshot>.Fail($"Project path is invalid: {ex.Message}");
        }

        lock (_lock)
        {
            if (_projects.TryGetValue(key.FullPath, out ProjectEntry? entry)
                && entry.Key.Matches(key))
            {
                return Result<ProjectSnapshot>.Ok(entry.Snapshot);
            }
        }

        Result<ProjectSnapshot> read = reader(key.FullPath);
        if (read.Success && read.Value != null)
        {
            lock (_lock)
            {
                _projects[key.FullPath] = new ProjectEntry(key, read.Value);
            }
        }

        return read;
    }

    public Result<PlaybackArchiveSnapshot> GetOrReadPlayback(
        string path,
        Func<string, Result<PlaybackArchiveSnapshot>> reader) =>
        GetOrReadPlayback(path, stamps: null, reader);

    /// <inheritdoc cref="GetOrReadProject(string, DataRootStampSet?, Func{string, Result{ProjectSnapshot}})"/>
    public Result<PlaybackArchiveSnapshot> GetOrReadPlayback(
        string path,
        DataRootStampSet? stamps,
        Func<string, Result<PlaybackArchiveSnapshot>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        CacheKey key;
        try
        {
            key = CacheKey.Create(path, stamps);
        }
        catch (Exception ex)
        {
            return Result<PlaybackArchiveSnapshot>.Fail($"Playback path is invalid: {ex.Message}");
        }

        lock (_lock)
        {
            if (_playbacks.TryGetValue(key.FullPath, out PlaybackEntry? entry)
                && entry.Key.Matches(key))
            {
                return Result<PlaybackArchiveSnapshot>.Ok(entry.Snapshot);
            }
        }

        Result<PlaybackArchiveSnapshot> read = reader(key.FullPath);
        if (read.Success && read.Value != null)
        {
            lock (_lock)
            {
                _playbacks[key.FullPath] = new PlaybackEntry(key, read.Value);
            }
        }

        return read;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _projects.Clear();
            _playbacks.Clear();
        }
    }

    private sealed record CacheKey(string FullPath, DateTime LastWriteTimeUtc, long Length)
    {
        public static CacheKey Create(string path, DataRootStampSet? stamps)
        {
            string fullPath = Path.GetFullPath(path);
            if (stamps != null && stamps.TryGet(fullPath, out FileStamp stamp))
            {
                return new CacheKey(fullPath, stamp.LastWriteTimeUtc, stamp.Length);
            }

            FileInfo info = new(fullPath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Archive file was not found.", fullPath);
            }

            return new CacheKey(fullPath, info.LastWriteTimeUtc, info.Length);
        }

        public bool Matches(CacheKey other) =>
            LastWriteTimeUtc == other.LastWriteTimeUtc
            && Length == other.Length;
    }

    private sealed record ProjectEntry(CacheKey Key, ProjectSnapshot Snapshot);

    private sealed record PlaybackEntry(CacheKey Key, PlaybackArchiveSnapshot Snapshot);
}
