using System.Text.Json;

namespace Rukari.CharacterVoice.Core;

internal sealed record VoicePublicationResult(string ResourceRoot, int Copied, int Total, bool Unchanged);

internal static partial class ProjectVoiceImportPolicy
{
    // A sibling of the archive, outside AA's unpacked <name>/ resource directory. AA may rebuild that directory.
    internal const string PublishedDirectorySuffix = ".rukari";
    private sealed record FileStamp(string Path, long Length, long Ticks);
    private sealed record PublicationCache(FileStamp SourceIndex, FileStamp TargetIndex, FileStamp[] Audio);
    private static readonly object PublicationGate = new();
    private static readonly Dictionary<string, PublicationCache> PublicationCacheByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileStamp> VerifiedPublishedAudio = new(StringComparer.OrdinalIgnoreCase);

    internal static string PublishedRootForArchive(string archivePath)
    {
        string full = NormalizeIdentity(archivePath);
        if (!Path.GetExtension(full).Equals(".aas", StringComparison.OrdinalIgnoreCase))
            throw new IOException("配音发布目标必须是游戏实际写出的 AAS。");
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + PublishedDirectorySuffix);
    }

    internal static string PublishedRootForPlayback(string liveRoot) => NormalizeIdentity(liveRoot) + PublishedDirectorySuffix;

    // Validate an observed resource root against the session that is actually compiling. Never derive a
    // substitute root when the current one disagrees; a preview may have changed the shared resource manager.
    internal static bool MatchesCompilingProject(string liveRoot, string projectFile, string? expectedName = null)
    {
        if (string.IsNullOrWhiteSpace(projectFile) || !Path.IsPathFullyQualified(projectFile)
            || string.IsNullOrWhiteSpace(liveRoot) || !Path.IsPathFullyQualified(liveRoot)) return false;
        string extension = Path.GetExtension(projectFile);
        if (!extension.Equals(".aap", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".aap2", StringComparison.OrdinalIgnoreCase)) return false;
        string full = NormalizeIdentity(projectFile);
        string name = Path.GetFileNameWithoutExtension(full);
        if (expectedName is not null && !name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)) return false;
        string expectedRoot = Path.Combine(Path.GetDirectoryName(full)!, name);
        return NormalizeIdentity(liveRoot).Equals(expectedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies only this mod's indexed audio, after an observed official AAS promotion. Never writes the archive.</summary>
    internal static VoicePublicationResult PublishForArchive(string projectRoot, string expectedName, string archivePath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string source = NormalizeProjectRoot(projectRoot);
        string archive = NormalizeIdentity(archivePath);
        string target = PublishedRootForArchive(archive);
        if (!string.Equals(Path.GetFileNameWithoutExtension(archive), expectedName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(source), expectedName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("发布目标与当前编译工程不匹配，未同步语音。");
        if (IsSameOrChild(source, target) || IsSameOrChild(target, source))
            throw new IOException("工程与作品语音目录必须相互独立。");
        RejectReparsePoints(archive);
        if (!File.Exists(archive)) throw new IOException("游戏尚未写出 AAS，未同步语音。");
        string sourceIndex = ContainedPath(source, DirectoryName + "/index.json");
        RejectReparsePoints(sourceIndex);
        if (!File.Exists(sourceIndex)) return new(target, 0, 0, true);
        string targetIndex = ContainedPath(target, DirectoryName + "/index.json");
        string cacheKey = source + "\n" + target;

        // A busy import is retried by the next save, rather than blocking the Unity thread for 30 seconds.
        using FileStream sourceLock = AcquireLock(ContainedPath(source, DirectoryName + "/.import.lock"), token, TimeSpan.Zero);
        lock (PublicationGate)
        {
            if (PublicationCacheByPair.TryGetValue(cacheKey, out PublicationCache? cached)
                && Matches(cached.SourceIndex) && Matches(cached.TargetIndex) && cached.Audio.All(Matches))
                return new(target, 0, cached.Audio.Length / 2, true);
        }
        var sourceEntries = ReadIndex(source);
        if (sourceEntries.Sum(entry => entry.Length) > MaximumTotalBytes)
            throw new IOException("本工程导入语音总量超过单次发布上限。");
        EnsureDirectory(ContainedPath(target, DirectoryName));
        using FileStream targetLock = AcquireLock(ContainedPath(target, DirectoryName + "/.import.lock"), token, TimeSpan.Zero);
        _ = ReadIndex(target); // Never overwrite an invalid existing catalog.
        string pendingDirectory = ContainedPath(target, DirectoryName + "/.publish-" + Guid.NewGuid().ToString("N"));
        EnsureDirectory(pendingDirectory);
        var temporaryFiles = new List<string>();
        var created = new List<Published>();
        var exported = new List<ProjectVoiceImportEntry>();
        var stamps = new List<FileStamp>();
        bool committed = false;
        try
        {
            foreach (ProjectVoiceImportEntry entry in sourceEntries)
            {
                token.ThrowIfCancellationRequested();
                string original = ContainedPath(source, entry.RelativePath);
                RejectReparsePoints(original);
                if (!IsAvailable(source, entry)) throw new IOException("有导入语音缺失或已改变，请重新导入后保存。");
                string extension = ProbeContainer(original) ?? Path.GetExtension(original);
                string relative = DirectoryName + "/" + entry.Hash + extension;
                string destination = ContainedPath(target, relative);
                if (File.Exists(destination))
                {
                    VerifyExisting(original, entry.Hash, entry.Length, token);
                    VerifyExisting(destination, entry.Hash, entry.Length, token);
                }
                else
                {
                    string pending = ContainedPath(pendingDirectory, entry.Hash + ".audio");
                    temporaryFiles.Add(pending);
                    string actualHash = CopySource(source, new(original, entry.RelativePath, entry.Length,
                        extension, entry.LastWriteTimeUtcTicks), pending, token);
                    if (actualHash != entry.Hash) throw new IOException("导入语音内容与索引不一致，未发布该批语音。");
                    Publish(pending, destination, entry.Hash, entry.Length, created, token);
                }
                exported.Add(entry with { RelativePath = relative, RestoredNative = false,
                    LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(destination).Ticks });
                stamps.Add(Stamp(original));
                stamps.Add(Stamp(destination));
            }
            string pendingIndex = ContainedPath(pendingDirectory, "index.json");
            temporaryFiles.Add(pendingIndex);
            WriteIndexFile(pendingIndex, exported);
            RejectReparsePoints(targetIndex);
            token.ThrowIfCancellationRequested();
            CommitIndex(pendingIndex, targetIndex);
            committed = true;
            lock (PublicationGate)
            {
                if (PublicationCacheByPair.Count >= 8) PublicationCacheByPair.Clear();
                PublicationCacheByPair[cacheKey] = new(Stamp(sourceIndex), Stamp(targetIndex), stamps.ToArray());
            }
            return new(target, created.Count, exported.Count, false);
        }
        finally
        {
            if (!committed)
                foreach (Published file in created.AsEnumerable().Reverse()) RollBackPublishedFile(target, file);
            foreach (string pending in temporaryFiles) SafeDeleteOwnedFile(pendingDirectory, pending);
            try { RejectReparsePoints(pendingDirectory); Directory.Delete(pendingDirectory, recursive: false); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Portable reads tolerate ZIP timestamp rounding, but require the actual content hash. No writes on playback.</summary>
    internal static bool TryResolvePublishedVoice(string playbackRoot, string resourceKey, out string path)
    {
        path = string.Empty;
        if (!IsImportKey(resourceKey)) return false;
        string hash = resourceKey[ResourceKeyPrefix.Length..];
        if (!ValidHash(hash)) return false;
        try
        {
            string root = NormalizeProjectRoot(PublishedRootForPlayback(playbackRoot));
            ProjectVoiceImportEntry? entry = ReadIndex(root).FirstOrDefault(item => item.Hash == hash);
            if (entry is null || entry.RestoredNative) return false;
            string candidate = ContainedPath(root, entry.RelativePath);
            FileStamp stamp = Stamp(candidate);
            if (stamp.Length != entry.Length) return false;
            lock (PublicationGate)
            {
                if (!VerifiedPublishedAudio.TryGetValue(candidate, out FileStamp? prior) || prior != stamp)
                {
                    VerifyExisting(candidate, hash, entry.Length, CancellationToken.None);
                    if (VerifiedPublishedAudio.Count >= MaximumIndexEntries) VerifiedPublishedAudio.Clear();
                    VerifiedPublishedAudio[candidate] = stamp;
                }
            }
            path = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { return false; }
    }

    private static bool IsSameOrChild(string root, string candidate) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static FileStamp Stamp(string path)
    {
        RejectReparsePoints(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("语音发布文件不存在。", path);
        return new(path, info.Length, info.LastWriteTimeUtc.Ticks);
    }
    private static bool Matches(FileStamp expected)
    {
        try { return Stamp(expected.Path) == expected; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
}
