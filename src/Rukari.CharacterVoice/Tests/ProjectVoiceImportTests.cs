using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rukari.CharacterVoice.Core;

internal static class ProjectVoiceImportTests
{
    internal static void RunAll()
    {
        ImportUsesContentKeysAndNeverChangesManifest();
        NativeMissingFilesRestoreWithoutDuplicatingAudio();
        AmbiguousNativeNamesAreOnlyPrivateImports();
        ChangedSourceAndCancellationLeaveNoPublishedBatch();
        CorruptDestinationIsNeverOverwrittenAndBatchRollsBack();
        ConcurrentImportsMergeIndexesAndProjectsRemainIsolated();
        UntrustedPathsAndCorruptIndexesFailClosed();
        ReparsePathsAreRejectedWhenSupported();
        ContainerSniffingFollowsTheBytes();
        MislabeledContainerIsStoredAndHealedByExtension();
    }

    private static void ContainerSniffingFollowsTheBytes()
    {
        // The native decoder is chosen from the extension (Utils.Util.GetAudioFormat), so the stored
        // extension has to describe the container, and an unnamed one must stay unidentified.
        Equal(".wav", ProjectVoiceImportPolicy.ContainerExtension(Signature("wav")));
        Equal(".ogg", ProjectVoiceImportPolicy.ContainerExtension(Signature("ogg")));
        Equal(".mp3", ProjectVoiceImportPolicy.ContainerExtension(Signature("mp3")));
        Equal<string?>(null, ProjectVoiceImportPolicy.ContainerExtension(Signature("opus")));
        Equal<string?>(null, ProjectVoiceImportPolicy.ContainerExtension(new byte[128]));
        Equal<string?>(null, ProjectVoiceImportPolicy.ContainerExtension(Array.Empty<byte>()));
    }

    private static void MislabeledContainerIsStoredAndHealedByExtension()
    {
        // A rip hands out Ogg Vorbis bytes under a .wav name; the wrong decoder answers
        // "Unsupported file or audio format" with an empty clip.
        using var fixture = new Fixture();
        VoiceImportResult imported = fixture.Import(fixture.Audio("lobby/CH0335_MemorialLobby_1_1.wav", 31, "ogg"));
        Equal(1, imported.ImportedCount);
        Equal(1, imported.CorrectedContainers);
        Equal(0, imported.UnrecognizedContainers);
        var entry = ProjectVoiceImportPolicy.ReadCatalog(fixture.Project).Single();
        Equal("lobby/CH0335_MemorialLobby_1_1.wav", entry.DisplayName);
        True(entry.RelativePath.EndsWith(".ogg", StringComparison.Ordinal));
        Equal(1, Directory.GetFiles(Path.Combine(fixture.Project, "rukari-voices"), "*.ogg").Length);
        Equal(0, Directory.GetFiles(Path.Combine(fixture.Project, "rukari-voices"), "*.wav").Length);
        True(ProjectVoiceImportPolicy.TryResolvePath(fixture.Project, entry.ResourceKey, out string path));
        Equal(".ogg", Path.GetExtension(path));

        // A container we cannot identify keeps the source extension instead of being rejected.
        using var unknown = new Fixture();
        VoiceImportResult plain = unknown.Import(unknown.Audio("plain.wav", 33));
        Equal(0, plain.CorrectedContainers);
        Equal(1, plain.UnrecognizedContainers);
        True(ProjectVoiceImportPolicy.ReadCatalog(unknown.Project).Single().RelativePath.EndsWith(".wav", StringComparison.Ordinal));

        // A voice stored before the container was understood is repaired on its first lookup, and the
        // content-hash key keeps every existing binding valid.
        using var legacy = new Fixture();
        VoiceImportSource source = legacy.Audio("voice.wav", 32, "ogg");
        byte[] bytes = File.ReadAllBytes(source.FullPath);
        string hash = Hash(bytes);
        string directory = Path.Combine(legacy.Project, "rukari-voices");
        Directory.CreateDirectory(directory);
        string stored = Path.Combine(directory, hash + ".wav");
        File.Copy(source.FullPath, stored);
        File.WriteAllText(Path.Combine(directory, "index.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[]
            {
                new
                {
                    Hash = hash, RelativePath = "rukari-voices/" + hash + ".wav", DisplayName = "voice.wav",
                    Length = bytes.Length, LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(stored).Ticks, RestoredNative = false,
                },
            },
        }));
        True(ProjectVoiceImportPolicy.TryResolvePath(legacy.Project, "rukari-import/" + hash, out string healed));
        Equal(".ogg", Path.GetExtension(healed));
        Equal(1, Directory.GetFiles(directory, "*.ogg").Length);
        Equal(0, Directory.GetFiles(directory, "*.wav").Length);
        True(ProjectVoiceImportPolicy.ReadCatalog(legacy.Project).Single().RelativePath.EndsWith(".ogg", StringComparison.Ordinal));
    }

    private static void ImportUsesContentKeysAndNeverChangesManifest()
    {
        using var fixture = new Fixture();
        string originalManifest = "{\"VoiceOverrides\":[],\"keep\":\"unchanged\"}";
        File.WriteAllText(Path.Combine(fixture.Project, "manifest.json"), originalManifest);
        VoiceImportSource one = fixture.Audio("nested/对白.WAV", 10);
        VoiceImportSource duplicate = fixture.Audio("different/another.wav", 10);
        VoiceImportResult result = fixture.Import(one, duplicate);
        Equal(1, result.ImportedCount);
        Equal(1, result.AlreadyPresentCount);
        Equal(originalManifest, File.ReadAllText(Path.Combine(fixture.Project, "manifest.json")));
        var entry = ProjectVoiceImportPolicy.ReadCatalog(fixture.Project).Single();
        Equal("nested/对白.WAV", entry.DisplayName);
        Equal("rukari-import/" + Hash(File.ReadAllBytes(one.FullPath)), entry.ResourceKey);
        True(ProjectVoiceImportPolicy.TryResolvePath(fixture.Project, entry.ResourceKey, out string path));
        Equal(".wav", Path.GetExtension(path));
        Equal(1, Directory.GetFiles(Path.Combine(fixture.Project, "rukari-voices"), "*.wav").Length);
        byte[] index = File.ReadAllBytes(fixture.IndexPath);
        VoiceImportResult second = fixture.Import(one);
        Equal(0, second.ImportedCount);
        Equal(1, second.AlreadyPresentCount);
        True(index.SequenceEqual(File.ReadAllBytes(fixture.IndexPath)));
        File.Delete(path);
        True(!ProjectVoiceImportPolicy.TryResolvePath(fixture.Project, entry.ResourceKey, out _));
        fixture.Import(one);
        True(ProjectVoiceImportPolicy.TryResolvePath(fixture.Project, entry.ResourceKey, out _));
    }

    private static void NativeMissingFilesRestoreWithoutDuplicatingAudio()
    {
        using var fixture = new Fixture();
        fixture.Manifest("voices\\001.ogg");
        string manifest = File.ReadAllText(Path.Combine(fixture.Project, "manifest.json"));
        VoiceImportSource source = fixture.Audio("character/001.ogg", 11);
        VoiceImportResult result = fixture.Import(source);
        Equal(1, result.RestoredNativeCount);
        var entry = ProjectVoiceImportPolicy.ReadCatalog(fixture.Project).Single();
        Equal("voices/001.ogg", entry.RelativePath);
        True(entry.RestoredNative);
        True(File.ReadAllBytes(source.FullPath).SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Project, "voices", "001.ogg"))));
        Equal(0, Directory.GetFiles(Path.Combine(fixture.Project, "rukari-voices"), "*.ogg").Length);
        Equal(manifest, File.ReadAllText(Path.Combine(fixture.Project, "manifest.json")));

        // Existing official bytes win even when the chosen folder has a different file of that name.
        using var existing = new Fixture();
        existing.Manifest("voices/001.ogg");
        Directory.CreateDirectory(Path.Combine(existing.Project, "voices"));
        byte[] original = { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(existing.Project, "voices", "001.ogg"), original);
        VoiceImportResult imported = existing.Import(existing.Audio("001.ogg", 42));
        Equal(0, imported.RestoredNativeCount);
        True(File.ReadAllBytes(Path.Combine(existing.Project, "voices", "001.ogg")).SequenceEqual(original));
        True(ProjectVoiceImportPolicy.ReadCatalog(existing.Project).Single().RelativePath.StartsWith("rukari-voices/", StringComparison.Ordinal));
    }

    private static void AmbiguousNativeNamesAreOnlyPrivateImports()
    {
        using var fixture = new Fixture();
        fixture.Manifest("voices/line.ogg");
        VoiceImportResult result = fixture.Import(fixture.Audio("a/line.ogg", 13), fixture.Audio("b/line.ogg", 14));
        Equal(2, result.AmbiguousRestoreCount);
        Equal(0, result.RestoredNativeCount);
        True(!File.Exists(Path.Combine(fixture.Project, "voices", "line.ogg")));
        Equal(2, ProjectVoiceImportPolicy.ReadCatalog(fixture.Project).Count);
        using var multipleTargets = new Fixture();
        multipleTargets.Manifest("voices/a/line.ogg", "voices/b/line.ogg");
        Equal(1, multipleTargets.Import(multipleTargets.Audio("line.ogg", 17)).AmbiguousRestoreCount);
    }

    private static void ChangedSourceAndCancellationLeaveNoPublishedBatch()
    {
        using var fixture = new Fixture();
        VoiceImportSource first = fixture.Audio("first.wav", 18);
        VoiceImportSource changed = fixture.Audio("second.wav", 19);
        File.WriteAllBytes(changed.FullPath, new byte[] { 1 });
        Throws<IOException>(() => fixture.Import(first, changed));
        True(!File.Exists(fixture.IndexPath));
        Equal(0, Directory.GetFiles(Path.Combine(fixture.Project, "rukari-voices"), "*.wav").Length);
        Equal(0, Directory.GetDirectories(Path.Combine(fixture.Project, "rukari-voices"), ".pending-*").Length);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Throws<OperationCanceledException>(() => ProjectVoiceImportPolicy.ImportAsync(new(fixture.Project), fixture.Source,
            new[] { first }, 0, cancel.Token).GetAwaiter().GetResult());
        True(!File.Exists(fixture.IndexPath));
    }

    private static void CorruptDestinationIsNeverOverwrittenAndBatchRollsBack()
    {
        using var fixture = new Fixture();
        VoiceImportSource initial = fixture.Audio("existing.wav", 21);
        fixture.Import(initial);
        var initialEntry = ProjectVoiceImportPolicy.ReadCatalog(fixture.Project).Single();
        string existingPath = Path.Combine(fixture.Project, initialEntry.RelativePath);
        byte[] corrupted = { 91, 92 };
        File.WriteAllBytes(existingPath, corrupted);
        byte[] oldIndex = File.ReadAllBytes(fixture.IndexPath);
        VoiceImportSource next = fixture.Audio("new.wav", 22);
        Throws<IOException>(() => fixture.Import(next, initial));
        True(File.ReadAllBytes(existingPath).SequenceEqual(corrupted));
        True(oldIndex.SequenceEqual(File.ReadAllBytes(fixture.IndexPath)));
        string newPath = Path.Combine(fixture.Project, "rukari-voices", Hash(File.ReadAllBytes(next.FullPath)) + ".wav");
        True(!File.Exists(newPath));
        True(!ProjectVoiceImportPolicy.TryResolvePath(fixture.Project, initialEntry.ResourceKey, out _));
    }

    private static void ConcurrentImportsMergeIndexesAndProjectsRemainIsolated()
    {
        using var fixture = new Fixture();
        VoiceImportSource a = fixture.Audio("a.wav", 23);
        VoiceImportSource b = fixture.Audio("b.mp3", 24);
        Task<VoiceImportResult> first = ProjectVoiceImportPolicy.ImportAsync(new(fixture.Project), fixture.Source, new[] { a }, 0, CancellationToken.None);
        Task<VoiceImportResult> second = ProjectVoiceImportPolicy.ImportAsync(new(fixture.Project), fixture.Source, new[] { b }, 0, CancellationToken.None);
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        Equal(2, ProjectVoiceImportPolicy.ReadCatalog(fixture.Project).Count);
        string otherProject = Path.Combine(fixture.Root, "other-project");
        Directory.CreateDirectory(otherProject);
        var entry = ProjectVoiceImportPolicy.ReadCatalog(fixture.Project)[0];
        True(!ProjectVoiceImportPolicy.TryResolvePath(otherProject, entry.ResourceKey, out _));
        Equal(fixture.Project, first.Result.ProjectRoot);
    }

    /// <summary>
    /// Measured 2026-09-19: editor preview played the imported voice while formal playback
    /// silently skipped it, because the live override root during playback is the played
    /// package, which never carries <c>rukari-voices/</c>. The binding is a content hash, so
    /// resolving it under any root this session imported into is identity, not a guess.
    /// </summary>
    internal static void ImportedVoiceResolvesAcrossKnownProjectRoots()
    {
        using var played = new Fixture();
        using var authoring = new Fixture();
        authoring.Import(authoring.Audio("lobby/CH0335_MemorialLobby_1_1.wav", 41));
        var entry = ProjectVoiceImportPolicy.ReadCatalog(authoring.Project).Single();

        // The played package does not own this voice...
        True(!ProjectVoiceImportPolicy.TryResolvePath(played.Project, entry.ResourceKey, out _));
        // ...and neither does an unrelated root.
        True(!ProjectVoiceImportPolicy.TryResolvePath(played.Project, entry.ResourceKey, out _));

        // ...but a root that imported it resolves the very same audio.
        True(ProjectVoiceImportPolicy.TryResolveAcrossRoots(
            new[] { played.Project, authoring.Project }, entry.ResourceKey, out string path, out string usedRoot));
        Equal(".wav", Path.GetExtension(path));
        True(path.StartsWith(Path.GetFullPath(authoring.Project), StringComparison.OrdinalIgnoreCase));
        True(usedRoot.Length != 0);

        // Nothing owns the hash: fail closed and never invent a path.
        True(!ProjectVoiceImportPolicy.TryResolveAcrossRoots(
            new[] { played.Project }, entry.ResourceKey, out string missing, out string missingRoot));
        Equal(string.Empty, missing);
        Equal(string.Empty, missingRoot);

        // Foreign keys and an empty candidate list never resolve through this path.
        True(!ProjectVoiceImportPolicy.TryResolveAcrossRoots(
            new[] { authoring.Project }, "voices/001.ogg", out _, out _));
        True(!ProjectVoiceImportPolicy.TryResolveAcrossRoots(
            Array.Empty<string>(), entry.ResourceKey, out _, out _));
    }

    private static void UntrustedPathsAndCorruptIndexesFailClosed()
    {
        using var fixture = new Fixture();
        fixture.Manifest("../escape.wav", "other/nonvoice.ogg", "voices/okay.ogg");
        VoiceImportSource source = fixture.Audio("okay.ogg", 25);
        fixture.Import(source);
        VoiceImportResult unrelated = fixture.Import(fixture.Audio("nonvoice.ogg", 27));
        Equal(0, unrelated.RestoredNativeCount);
        True(!File.Exists(Path.Combine(fixture.Project, "other", "nonvoice.ogg")));
        True(!File.Exists(Path.Combine(fixture.Root, "escape.wav")));
        Throws<IOException>(() => fixture.Import(source with { RelativePath = "../okay.ogg" }));
        Throws<IOException>(() => fixture.Import(source with { RelativePath = "CON.ogg" }));
        True(!ProjectVoiceImportPolicy.TryResolvePath(fixture.Project, "rukari-import/../outside", out _));
        string badIndex = "{\"Version\":1,\"Entries\":[{\"Hash\":\"" + new string('a', 64)
            + "\",\"RelativePath\":\"../escape.wav\",\"DisplayName\":\"escape.wav\",\"Length\":5,\"LastWriteTimeUtcTicks\":1,\"RestoredNative\":true}]}";
        File.WriteAllText(fixture.IndexPath, badIndex);
        Throws<IOException>(() => fixture.Import(source));
        Equal(badIndex, File.ReadAllText(fixture.IndexPath));
    }

    private static void ReparsePathsAreRejectedWhenSupported()
    {
        using var fixture = new Fixture();
        string link = Path.Combine(fixture.Root, "linked-project");
        try { Directory.CreateSymbolicLink(link, fixture.Project); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Console.WriteLine("SKIP import symlink fixture: platform permission unavailable");
            return;
        }
        try { Throws<IOException>(() => ProjectVoiceImportPolicy.NormalizeProjectRoot(link)); }
        finally { Directory.Delete(link); }
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>Container signatures a real rip would produce, for the import/repair fixtures.</summary>
    private static byte[] Signature(string container)
    {
        var bytes = new List<byte>();
        switch (container)
        {
            case "wav":
                bytes.AddRange(Encoding.ASCII.GetBytes("RIFF"));
                bytes.AddRange(new byte[4]);
                bytes.AddRange(Encoding.ASCII.GetBytes("WAVE"));
                break;
            case "ogg":
                bytes.AddRange(Encoding.ASCII.GetBytes("OggS"));
                bytes.AddRange(new byte[24]);
                bytes.Add(1);
                bytes.AddRange(Encoding.ASCII.GetBytes("vorbis"));
                break;
            case "opus":
                bytes.AddRange(Encoding.ASCII.GetBytes("OggS"));
                bytes.AddRange(new byte[24]);
                bytes.AddRange(Encoding.ASCII.GetBytes("OpusHead"));
                break;
            case "mp3":
                bytes.AddRange(Encoding.ASCII.GetBytes("ID3"));
                bytes.AddRange(new byte[7]);
                break;
        }
        return bytes.ToArray();
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Import assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rukari-voice-import-tests-" + Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "project");
        internal string Source => Path.Combine(Root, "source");
        internal string IndexPath => Path.Combine(Project, "rukari-voices", "index.json");
        internal Fixture() { Directory.CreateDirectory(Project); Directory.CreateDirectory(Source); }
        internal void Manifest(params string[] entries) => File.WriteAllText(Path.Combine(Project, "manifest.json"),
            JsonSerializer.Serialize(new { VoiceOverrides = entries }));
        internal VoiceImportSource Audio(string relative, byte seed, string container = "")
        {
            string path = Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = Enumerable.Range(0, 4096).Select(index => (byte)(seed + index % 17)).ToArray();
            if (container.Length != 0) Signature(container).CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);
            return new(path, relative, bytes.Length, Path.GetExtension(path).ToLowerInvariant(), File.GetLastWriteTimeUtc(path).Ticks);
        }
        internal VoiceImportResult Import(params VoiceImportSource[] sources) => ProjectVoiceImportPolicy.ImportAsync(
            new(Project), Source, sources, 0, CancellationToken.None).GetAwaiter().GetResult();
        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("rukari-voice-import-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup target.");
            Directory.Delete(full, recursive: true);
        }
    }
}
