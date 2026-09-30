using System.Security.Cryptography;
using System.Text.Json;

namespace Rukari.CharacterVoice.Core;

internal sealed record VoiceImportProject(string RootPath)
{
    internal string Identity => ProjectVoiceImportPolicy.NormalizeIdentity(RootPath);
}

internal sealed record VoiceImportSource(string FullPath, string RelativePath, long Length, string Extension, long LastWriteTimeUtcTicks = 0);
internal sealed record VoiceImportResult(string ProjectRoot, int ImportedCount, int AlreadyPresentCount,
    int RestoredNativeCount, int AmbiguousRestoreCount, int SkippedFiles, int TotalFiles,
    int CorrectedContainers = 0, int UnrecognizedContainers = 0);
internal sealed record ProjectVoiceImportEntry(string Hash, string RelativePath, string DisplayName, long Length,
    long LastWriteTimeUtcTicks, bool RestoredNative = false)
{
    public string ResourceKey => ProjectVoiceImportPolicy.ResourceKeyPrefix + Hash;
}

/// <summary>
/// Pure managed project storage. The caller captures a project on the Unity thread; this code
/// never accesses native objects. Audio is immutable, and index replacement is the commit point.
/// </summary>
internal static partial class ProjectVoiceImportPolicy
{
    internal const string ResourceKeyPrefix = "rukari-import/";
    internal const string DirectoryName = "rukari-voices";
    internal const int MaximumFiles = 2048;
    internal const int MaximumIndexEntries = 20000;
    internal const long MaximumFileBytes = 256L * 1024 * 1024;
    internal const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private const int ContainerProbeBytes = 256;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record IndexDocument(int Version, List<ProjectVoiceImportEntry> Entries);
    private sealed record Staged(VoiceImportSource Source, string Path, string Hash);
    private sealed record Published(string Path, string Hash, long Length);

    internal static bool IsImportKey(string? key) => key?.StartsWith(ResourceKeyPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// True when a manifest <c>VoiceOverrides</c> entry is a project voice path this module is allowed to manage:
    /// the observed native layout owns <c>voices/</c>, so anything else stays untouched even if it is contained.
    /// Shared with the cleanup policy so both agree on what a voice entry is.
    /// </summary>
    internal static bool IsProjectVoicePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        string normalized = relative.Replace('\\', '/');
        return ValidRelativePath(normalized)
            && normalized.StartsWith("voices/", StringComparison.OrdinalIgnoreCase)
            && SupportedExtension(Path.GetExtension(normalized));
    }

    internal static string NormalizeProjectRoot(string root)
    {
        string full = NormalizeIdentity(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("当前项目目录不存在。");
        RejectReparsePoints(full);
        return full;
    }

    // Main-thread identity polling uses only managed lexical path operations, never disk I/O.
    internal static string NormalizeIdentity(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new IOException("当前项目没有可用的绝对目录。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    internal static Task<VoiceImportResult> ImportAsync(VoiceImportProject project, string sourceRoot,
        IReadOnlyList<VoiceImportSource> sources, int skippedFiles, CancellationToken cancellationToken)
    {
        // Snapshot all managed values before dispatch. Never allow caller mutation of a pending batch.
        var capturedProject = new VoiceImportProject(NormalizeIdentity(project.RootPath));
        string capturedSource = NormalizeIdentity(sourceRoot);
        VoiceImportSource[] files = sources.ToArray();
        return Task.Run(() => Import(capturedProject, capturedSource, files, skippedFiles, cancellationToken), cancellationToken);
    }

    private static VoiceImportResult Import(VoiceImportProject project, string sourceRoot,
        VoiceImportSource[] sources, int skippedFiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = NormalizeProjectRoot(project.RootPath);
        sourceRoot = NormalizeProjectRoot(sourceRoot);
        ValidateSources(sourceRoot, sources);
        string directory = ContainedPath(root, DirectoryName);
        EnsureDirectory(directory);
        string lockPath = ContainedPath(directory, ".import.lock");
        RejectReparsePoints(lockPath);
        // A persistent empty lock file avoids unlink/recreate races between processes and mod reloads.
        using FileStream importLock = AcquireLock(lockPath, cancellationToken);
        RejectReparsePoints(directory);
        string indexPath = ContainedPath(directory, "index.json");
        List<ProjectVoiceImportEntry> entries = ReadIndex(root);
        var byHash = entries.ToDictionary(entry => entry.Hash, StringComparer.Ordinal);
        Dictionary<string, string[]> restoreTargets = ReadRestoreTargets(root);
        var sourceNames = sources.GroupBy(item => Path.GetFileName(item.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var created = new List<Published>();
        var staged = new List<Staged>();
        var temporaryFiles = new List<string>();
        string temporaryDirectory = ContainedPath(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        EnsureDirectory(temporaryDirectory);
        bool committed = false;
        bool indexChanged = false;
        int imported = 0, existing = 0, restored = 0, ambiguous = 0, corrected = 0, unrecognized = 0;
        try
        {
            // Validate every source before publishing any destination or index entry.
            foreach (VoiceImportSource source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string temporaryPath = ContainedPath(temporaryDirectory, staged.Count + ".audio");
                temporaryFiles.Add(temporaryPath);
                string hash = CopySource(sourceRoot, source, temporaryPath, cancellationToken);
                staged.Add(new(source, temporaryPath, hash));
            }
            foreach (Staged item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(item.Source.RelativePath);
                // The stored name follows the bytes, not the name the file arrived with; a container we
                // cannot identify keeps the source extension so nothing that used to import is rejected.
                string? container = ProbeContainer(item.Path);
                if (container is null) unrecognized++;
                string extension = container ?? item.Source.Extension;
                string? restoreRelative = null;
                if (restoreTargets.TryGetValue(name, out string[]? candidates))
                {
                    if (candidates.Length != 1 || sourceNames[name] != 1) ambiguous++;
                    else restoreRelative = candidates[0];
                }

                if (byHash.TryGetValue(item.Hash, out ProjectVoiceImportEntry? prior))
                {
                    if (MismatchedContainer(root, prior) is string priorContainer)
                    {
                        ProjectVoiceImportEntry healedPrior = HealStoredExtension(root, entries, prior, priorContainer, cancellationToken);
                        if (!ReferenceEquals(healedPrior, prior))
                        {
                            prior = healedPrior;
                            byHash[item.Hash] = healedPrior;
                            indexChanged = true;
                            corrected++;
                        }
                    }
                    string priorPath = ContainedPath(root, prior.RelativePath);
                    bool repaired = false;
                    if (!File.Exists(priorPath))
                    {
                        repaired = Publish(item.Path, priorPath, item.Hash, item.Source.Length, created, cancellationToken);
                        if (prior.RestoredNative && repaired) restored++;
                    }
                    else VerifyExisting(priorPath, item.Hash, item.Source.Length, cancellationToken);
                    long ticks = File.GetLastWriteTimeUtc(priorPath).Ticks;
                    if (prior.LastWriteTimeUtcTicks != ticks)
                    {
                        var refreshed = prior with { LastWriteTimeUtcTicks = ticks };
                        entries[entries.IndexOf(prior)] = refreshed;
                        byHash[item.Hash] = refreshed;
                        indexChanged = true;
                    }
                    existing++;
                    // An earlier import may predate the missing native binding. Restore it without
                    // changing the already published key or overwriting a native file.
                    if (restoreRelative is not null)
                    {
                        string restorePath = ContainedPath(root, restoreRelative);
                        if (!File.Exists(restorePath) && !repaired)
                        {
                            Publish(item.Path, restorePath, item.Hash, item.Source.Length, created, cancellationToken);
                            restored++;
                        }
                    }
                    continue;
                }

                string relative = restoreRelative ?? DirectoryName + "/" + item.Hash + extension;
                string destination = ContainedPath(root, relative);
                bool native = restoreRelative is not null;
                // A native file appearing after the manifest snapshot is never overwritten. It may
                // be used only if it contains the exact selected bytes, otherwise use private storage.
                if (native && File.Exists(destination) && !HasHash(destination, item.Hash, item.Source.Length, cancellationToken))
                {
                    relative = DirectoryName + "/" + item.Hash + extension;
                    destination = ContainedPath(root, relative);
                    native = false;
                }
                bool made = Publish(item.Path, destination, item.Hash, item.Source.Length, created, cancellationToken);
                if (!native && !extension.Equals(item.Source.Extension, StringComparison.Ordinal)) corrected++;
                if (native && made) restored++;
                var entry = new ProjectVoiceImportEntry(item.Hash, relative.Replace('\\', '/'), item.Source.RelativePath,
                    item.Source.Length, File.GetLastWriteTimeUtc(destination).Ticks, native);
                entries.Add(entry);
                byHash.Add(item.Hash, entry);
                imported++;
                indexChanged = true;
            }
            if (entries.Count > MaximumIndexEntries) throw new IOException("项目语音索引已达到安全读取上限。");
            cancellationToken.ThrowIfCancellationRequested();
            if (indexChanged)
            {
                string pendingIndex = ContainedPath(temporaryDirectory, "index.json");
                WriteIndexFile(pendingIndex, entries);
                RejectReparsePoints(indexPath);
                RejectReparsePoints(directory);
                cancellationToken.ThrowIfCancellationRequested();
                CommitIndex(pendingIndex, indexPath);
            }
            // Cancellation after this point must not report an uncommitted operation.
            committed = true;
            return new(root, imported, existing, restored, ambiguous, skippedFiles, sources.Length, corrected, unrecognized);
        }
        finally
        {
            if (!committed)
                foreach (Published file in created.AsEnumerable().Reverse()) RollBackPublishedFile(root, file);
            // Delete individual verified children; never recursively delete a computed directory.
            foreach (string path in temporaryFiles) SafeDeleteOwnedFile(temporaryDirectory, path);
            SafeDeleteOwnedFile(temporaryDirectory, Path.Combine(temporaryDirectory, "index.json"));
            try { RejectReparsePoints(temporaryDirectory); Directory.Delete(temporaryDirectory, recursive: false); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static IReadOnlyList<ProjectVoiceImportEntry> ReadCatalog(string projectRoot)
    {
        string root = NormalizeProjectRoot(projectRoot);
        return ReadIndex(root).Where(entry => IsAvailable(root, entry)).ToArray();
    }

    /// <summary>
    /// The container the bytes actually are. The native loader picks its decoder from the path extension
    /// (<c>Utils.Util.GetAudioFormat(over)</c>), so a file whose name lies about its container is handed to
    /// the wrong decoder and FMOD answers "Unsupported file or audio format" with an empty clip. Rips hand
    /// out Ogg Vorbis bytes named <c>.wav</c>, so the stored name has to follow the bytes.
    /// </summary>
    internal static string? ContainerExtension(ReadOnlySpan<byte> head)
    {
        if (AsciiAt(head, 0, "RIFF") && AsciiAt(head, 8, "WAVE")) return ".wav";
        // Unity's Ogg path decodes Vorbis; an Opus stream has to be reported as unrecognized rather than
        // stored as .ogg, where it would fail the same way as a mislabeled file.
        if (AsciiAt(head, 0, "OggS")) return ContainsAscii(head, "vorbis") ? ".ogg" : null;
        if (AsciiAt(head, 0, "ID3")) return ".mp3";
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return ".mp3";
        return null;
    }

    internal static bool TryResolvePath(string projectRoot, string resourceKey, out string path)
    {
        path = string.Empty;
        if (!IsImportKey(resourceKey)) return false;
        string hash = resourceKey[ResourceKeyPrefix.Length..];
        if (!ValidHash(hash)) return false;
        try
        {
            string root = NormalizeProjectRoot(projectRoot);
            List<ProjectVoiceImportEntry> entries = ReadIndex(root);
            ProjectVoiceImportEntry? entry = entries.FirstOrDefault(item => item.Hash == hash);
            if (entry is null) return false;
            // A voice stored before the container was understood is repaired on first use: the binding is a
            // content hash, so renaming the file keeps every voices.txt entry pointing at the same key.
            if (MismatchedContainer(root, entry) is string container) entry = HealWithLock(root, entries, entry, container);
            if (!IsAvailable(root, entry)) return false;
            path = ContainedPath(root, entry.RelativePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { return false; }
    }

    /// <summary>
    /// Resolves an imported voice by walking several candidate project roots (2026-09-19).
    /// <para>
    /// The binding is a content hash, so a hit under ANY project root is the same audio the
    /// author bound: this is identity, not a guess. It exists because the project root the
    /// runtime can observe is not always the project the voice was imported into — during
    /// formal playback the live override root is the played package, which has no
    /// <c>rukari-voices/</c> at all, so editor preview played the voice and playback silently
    /// skipped it ("Owned voice skipped and dialogue wait released: voice-file-not-found").
    /// </para>
    /// <para>
    /// Failed candidates stay silent on purpose: the caller decides what to report, and a
    /// lookup miss is normal for a root that simply does not own this voice.
    /// </para>
    /// </summary>
    internal static bool TryResolveAcrossRoots(
        IReadOnlyList<string> projectRoots, string resourceKey, out string path, out string usedRoot)
    {
        path = string.Empty;
        usedRoot = string.Empty;
        ArgumentNullException.ThrowIfNull(projectRoots);
        if (!IsImportKey(resourceKey)) return false;
        foreach (string root in projectRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!TryResolvePath(root, resourceKey, out string candidate)) continue;
            path = candidate;
            try { usedRoot = NormalizeProjectRoot(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { usedRoot = root; }
            return true;
        }
        return false;
    }

    /// <summary>
    /// The container extension a stored private voice should have, or null when its name already matches,
    /// when the caller may not rename it, or when the container could not be identified.
    /// </summary>
    private static string? MismatchedContainer(string root, ProjectVoiceImportEntry entry)
    {
        // The game's own manifest owns the path of a restored native voice; never rename those.
        if (entry.RestoredNative) return null;
        string? container = ProbeContainer(ContainedPath(root, entry.RelativePath));
        return container is not null && !Path.GetExtension(entry.RelativePath).Equals(container, StringComparison.Ordinal)
            ? container : null;
    }

    /// <summary>Repairs a stored extension while an import is not in flight, so the index write cannot race one.</summary>
    private static ProjectVoiceImportEntry HealWithLock(string root, List<ProjectVoiceImportEntry> entries,
        ProjectVoiceImportEntry entry, string container)
    {
        FileStream? importLock = null;
        try
        {
            // An import holds this lock for its whole batch; the next lookup repairs instead of blocking
            // the Unity thread behind it.
            try { importLock = AcquireLock(ContainedPath(root, DirectoryName + "/.import.lock"), CancellationToken.None, TimeSpan.Zero); }
            catch (IOException) { return entry; }
            ProjectVoiceImportEntry healed = HealStoredExtension(root, entries, entry, container, CancellationToken.None);
            if (ReferenceEquals(healed, entry)) return entry;
            try { WriteIndexInPlace(root, entries); }
            catch
            {
                // The index still names the old path, so put the file back rather than leave the two apart.
                try { File.Move(ContainedPath(root, healed.RelativePath), ContainedPath(root, entry.RelativePath), overwrite: false); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return healed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return entry; }
        finally { importLock?.Dispose(); }
    }

    /// <summary>Renames the stored file to the container's extension and records the new relative path.</summary>
    private static ProjectVoiceImportEntry HealStoredExtension(string root, List<ProjectVoiceImportEntry> entries,
        ProjectVoiceImportEntry entry, string container, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string relative = DirectoryName + "/" + entry.Hash + container;
        string destination = ContainedPath(root, relative);
        if (File.Exists(destination))
        {
            if (!HasHash(destination, entry.Hash, entry.Length, token)) return entry;
        }
        else
        {
            string current = ContainedPath(root, entry.RelativePath);
            RejectReparsePoints(current);
            RejectReparsePoints(destination);
            File.Move(current, destination, overwrite: false);
        }
        var healed = entry with { RelativePath = relative };
        int index = entries.IndexOf(entry);
        if (index >= 0) entries[index] = healed;
        return healed;
    }

    private static string? ProbeContainer(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Span<byte> head = stackalloc byte[ContainerProbeBytes];
            int read = stream.Read(head);
            return read <= 0 ? null : ContainerExtension(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool AsciiAt(ReadOnlySpan<byte> head, int offset, string text)
    {
        if (head.Length < offset + text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (head[offset + i] != text[i]) return false;
        return true;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> head, string text)
    {
        for (int start = 0; start + text.Length <= head.Length; start++)
        {
            int matched = 0;
            while (matched < text.Length && head[start + matched] == text[matched]) matched++;
            if (matched == text.Length) return true;
        }
        return false;
    }

    private static bool IsAvailable(string root, ProjectVoiceImportEntry entry)
    {
        try
        {
            string path = ContainedPath(root, entry.RelativePath);
            RejectReparsePoints(path);
            var info = new FileInfo(path);
            // Hashes are checked during publication. Metadata guards avoid hashing hundreds of MB
            // on Unity's main thread on each VoiceExists query; changed files require reimport.
            return info.Exists && info.Length == entry.Length && info.LastWriteTimeUtc.Ticks == entry.LastWriteTimeUtcTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static List<ProjectVoiceImportEntry> ReadIndex(string root)
    {
        string path = ContainedPath(root, DirectoryName + "/index.json");
        RejectReparsePoints(path);
        if (!File.Exists(path)) return new();
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (reader.Length > MaximumIndexBytes) throw new IOException("语音索引超过安全读取上限。");
        IndexDocument document = JsonSerializer.Deserialize<IndexDocument>(reader)
            ?? throw new IOException("语音索引为空。");
        if (document.Version != 1 || document.Entries is null || document.Entries.Count > MaximumIndexEntries)
            throw new IOException("语音索引版本或条目数无效。");
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProjectVoiceImportEntry entry in document.Entries)
        {
            if (entry is null || !ValidHash(entry.Hash) || !hashes.Add(entry.Hash)
                || entry.Length <= 0 || entry.Length > MaximumFileBytes || entry.LastWriteTimeUtcTicks <= 0
                || !ValidRelativePath(entry.DisplayName) || !ValidRelativePath(entry.RelativePath)
                || !SupportedExtension(Path.GetExtension(entry.RelativePath)))
                throw new IOException("语音索引包含无效条目；未覆盖原索引。");
            _ = ContainedPath(root, entry.RelativePath);
            if (!entry.RestoredNative && entry.RelativePath != DirectoryName + "/" + entry.Hash + Path.GetExtension(entry.RelativePath))
                throw new IOException("私有语音条目路径与内容键不一致。");
            if (entry.RestoredNative && !entry.RelativePath.StartsWith("voices/", StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复的原生语音必须位于项目 voices 目录。");
        }
        return document.Entries;
    }

    private static void WriteIndexFile(string path, List<ProjectVoiceImportEntry> entries)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new IndexDocument(1, entries), JsonOptions);
        if (bytes.Length > MaximumIndexBytes) throw new IOException("项目语音索引过大。");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        writer.Write(bytes);
        writer.Flush(flushToDisk: true);
    }

    private static void CommitIndex(string pendingPath, string indexPath)
    {
        if (File.Exists(indexPath)) File.Replace(pendingPath, indexPath, null);
        else File.Move(pendingPath, indexPath, overwrite: false);
    }

    /// <summary>Replaces the index outside an import batch, using the same staged write and atomic swap.</summary>
    private static void WriteIndexInPlace(string root, List<ProjectVoiceImportEntry> entries)
    {
        string pending = ContainedPath(root, DirectoryName + "/index.json.heal-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteIndexFile(pending, entries);
            string indexPath = ContainedPath(root, DirectoryName + "/index.json");
            RejectReparsePoints(indexPath);
            CommitIndex(pending, indexPath);
        }
        finally { SafeDeleteOwnedFile(root, pending); }
    }

    private static Dictionary<string, string[]> ReadRestoreTargets(string root)
    {
        string path = ContainedPath(root, "manifest.json");
        RejectReparsePoints(path);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (reader.Length > MaximumIndexBytes) throw new IOException("项目 manifest 过大，无法安全核对旧语音。");
        using JsonDocument manifest = JsonDocument.Parse(reader);
        if (!manifest.RootElement.TryGetProperty("VoiceOverrides", out JsonElement voices)) return new(StringComparer.OrdinalIgnoreCase);
        if (voices.ValueKind != JsonValueKind.Array || voices.GetArrayLength() > MaximumIndexEntries)
            throw new IOException("项目 VoiceOverrides 格式或条目数无效。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in voices.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            string relative = (value.GetString() ?? string.Empty).Replace('\\', '/');
            // The observed native project layout owns voices/. Do not use an arbitrary manifest
            // entry as permission to write audio elsewhere in the project (even if root-contained).
            if (!ValidRelativePath(relative) || !relative.StartsWith("voices/", StringComparison.OrdinalIgnoreCase)
                || !SupportedExtension(Path.GetExtension(relative))) continue;
            string target = ContainedPath(root, relative);
            RejectReparsePoints(target);
            if (!File.Exists(target)) paths.Add(relative);
        }
        return paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateSources(string sourceRoot, IReadOnlyList<VoiceImportSource> sources)
    {
        if (sources.Count == 0 || sources.Count > MaximumFiles) throw new IOException("需要 1 到 2048 个可用语音文件。");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VoiceImportSource source in sources)
        {
            if (source is null || !ValidRelativePath(source.RelativePath) || !SupportedExtension(source.Extension)
                || !Path.GetExtension(source.FullPath).Equals(source.Extension, StringComparison.OrdinalIgnoreCase)
                || source.Length <= 0 || source.Length > MaximumFileBytes)
                throw new IOException("文件扫描结果无效，请重新选择文件夹。");
            string expected = ContainedPath(sourceRoot, source.RelativePath);
            if (!Path.GetFullPath(source.FullPath).Equals(expected, StringComparison.OrdinalIgnoreCase) || !paths.Add(expected))
                throw new IOException("语音文件不在所选目录内，或扫描结果重复。");
            total = checked(total + source.Length);
            if (total > MaximumTotalBytes) throw new IOException("单次导入总量不能超过 2 GiB。");
        }
    }

    private static string CopySource(string sourceRoot, VoiceImportSource source, string target, CancellationToken token)
    {
        string path = ContainedPath(sourceRoot, source.RelativePath);
        RejectReparsePoints(path);
        var before = new FileInfo(path);
        if (!before.Exists || before.Length != source.Length
            || (source.LastWriteTimeUtcTicks != 0 && before.LastWriteTimeUtc.Ticks != source.LastWriteTimeUtcTicks))
            throw new IOException("语音文件自扫描后已改变，请重新扫描。");
        long ticks = before.LastWriteTimeUtc.Ticks;
        // On Windows this share mode denies concurrent write/delete handles while bytes are copied.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long copied = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            copied += count;
            if (copied > source.Length) throw new IOException("语音文件在复制时改变。");
            hash.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
        }
        output.Flush(flushToDisk: true);
        var after = new FileInfo(path);
        RejectReparsePoints(path);
        if (copied != source.Length || after.Length != source.Length || after.LastWriteTimeUtc.Ticks != ticks)
            throw new IOException("语音文件在复制时改变。");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool Publish(string staged, string destination, string hash, long length,
        List<Published> created, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        RejectReparsePoints(destination);
        if (File.Exists(destination)) { VerifyExisting(destination, hash, length, token); return false; }
        try { File.Move(staged, destination, overwrite: false); created.Add(new(destination, hash, length)); }
        catch (IOException) when (File.Exists(destination)) { VerifyExisting(destination, hash, length, token); return false; }
        VerifyExisting(destination, hash, length, token);
        return true;
    }

    private static bool HasHash(string path, string hash, long length, CancellationToken token)
    {
        RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != length) return false;
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        { token.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, read); }
        return Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyExisting(string path, string hash, long length, CancellationToken token)
    {
        if (!HasHash(path, hash, length, token)) throw new IOException("目标语音文件与内容键不一致；未覆盖该文件。");
    }

    private static FileStream AcquireLock(string path, CancellationToken token, TimeSpan? wait = null)
    {
        DateTime deadline = DateTime.UtcNow.Add(wait ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePoints(path);
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline)
            { if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested(); }
        }
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool SupportedExtension(string? extension) => extension is ".wav" or ".ogg" or ".mp3";
    private static bool ValidRelativePath(string? relative) => !string.IsNullOrWhiteSpace(relative)
        && relative.Length <= 4096 && !Path.IsPathRooted(relative) && !relative.Contains(':')
        && !relative.Any(char.IsControl) && !relative.Contains('\\')
        && !relative.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."
            || segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || IsWindowsDeviceName(segment));

    private static bool IsWindowsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)));
    }

    private static string ContainedPath(string root, string relative)
    {
        if (!ValidRelativePath(relative)) throw new IOException("语音路径必须是项目内的安全相对路径。");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("语音路径超出目标目录。");
        return full;
    }

    private static void EnsureDirectory(string path)
    {
        RejectReparsePoints(path);
        Directory.CreateDirectory(path);
        RejectReparsePoints(path);
    }

    private static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("语音导入不跟随符号链接或目录联接。");
            }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (parent == current) break;
            current = parent;
        }
    }

    private static void SafeDeleteOwnedFile(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string checkedPath = ContainedPath(root, relative);
            RejectReparsePoints(checkedPath);
            if (File.Exists(checkedPath)) File.Delete(checkedPath);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void RollBackPublishedFile(string root, Published file)
    {
        try
        {
            // Another application may have changed a newly created native file while import was
            // pending. Rollback must not remove those foreign bytes, even though our index failed.
            if (HasHash(file.Path, file.Hash, file.Length, CancellationToken.None))
                SafeDeleteOwnedFile(root, file.Path);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
