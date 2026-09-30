using System.Text;
using Rukari.CharacterVoice.Core;

internal static class VoicePublicationTests
{
    internal static void CompilationIdentityRejectsPreviewAndSameNameDifferentProjects()
    {
        using var f = new Fixture();
        Check(ProjectVoiceImportPolicy.MatchesCompilingProject(f.Project, f.Project + ".aap2", "work"));
        Check(ProjectVoiceImportPolicy.MatchesCompilingProject(f.Project, f.Project + ".aap"));
        Check(!ProjectVoiceImportPolicy.MatchesCompilingProject(f.Project, f.Project + ".aas"));
        Check(!ProjectVoiceImportPolicy.MatchesCompilingProject(f.Playback, f.Project + ".aap2"));
        Check(!ProjectVoiceImportPolicy.MatchesCompilingProject(f.Project, f.Project + ".aap2", "other"));
        Check(!ProjectVoiceImportPolicy.MatchesCompilingProject(f.Project,
            Path.Combine(f.Root, "another", "work.aap2")));
        Check(!ProjectVoiceImportPolicy.MatchesCompilingProject(f.Project, "work.aap2"));
        Check(!ProjectVoiceImportPolicy.MatchesCompilingProject("", f.Project + ".aap2"));
    }

    internal static void PublishesCompanionWithoutChangingOfficialFiles()
    {
        using var f = new Fixture();
        var entry = f.Import("one.wav", 12);
        byte[] archive = File.ReadAllBytes(f.Archive);
        byte[] manifest = File.ReadAllBytes(Path.Combine(f.Project, "manifest.json"));
        var result = f.Publish();
        Check(result.Copied == 1 && result.Total == 1 && !result.Unchanged);
        Check(result.ResourceRoot == f.Playback + ".rukari");
        Check(ProjectVoiceImportPolicy.TryResolvePublishedVoice(f.Playback, entry.ResourceKey, out string sound));
        Check(File.ReadAllBytes(sound).SequenceEqual(File.ReadAllBytes(Path.Combine(f.Project, entry.RelativePath))));
        Check(archive.SequenceEqual(File.ReadAllBytes(f.Archive)));
        Check(manifest.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Project, "manifest.json"))));
        Check(!Directory.Exists(f.Playback)); // Never write the game's unpacked resources.
    }

    internal static void RelocatedCompanionWorksWithoutEditorRootsAndIgnoresZipTimestampRounding()
    {
        using var f = new Fixture();
        var entry = f.Import("one.wav", 22);
        string exported = f.Publish().ResourceRoot;
        string movedPlayback = Path.Combine(f.Root, "other-machine", "renamed-work");
        string moved = movedPlayback + ".rukari";
        foreach (string file in Directory.GetFiles(exported, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            string dest = Path.Combine(moved, Path.GetRelativePath(exported, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
            File.SetLastWriteTimeUtc(dest, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        Check(ProjectVoiceImportPolicy.TryResolvePublishedVoice(movedPlayback, entry.ResourceKey, out string path));
        Check(path.StartsWith(moved, StringComparison.OrdinalIgnoreCase));
        // Read-only distribution: no healing or index writes on playback.
        Check(File.GetLastWriteTimeUtc(Path.Combine(moved, "rukari-voices", "index.json")).Year == 2020);
    }

    internal static void RestoredNativeEntriesAreExportedIntoOwnedStorage()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Project, "manifest.json"), "{\"VoiceOverrides\":[\"voices/old.wav\"]}");
        var entry = f.Import("old.wav", 32);
        Check(entry.RestoredNative);
        var result = f.Publish();
        var copied = ProjectVoiceImportPolicy.ReadCatalog(result.ResourceRoot).Single();
        Check(!copied.RestoredNative && copied.RelativePath.StartsWith("rukari-voices/", StringComparison.Ordinal));
        Check(!Directory.Exists(Path.Combine(result.ResourceRoot, "voices")));
    }

    internal static void FailedBatchPreservesPreviousPublicationAndForeignFiles()
    {
        using var f = new Fixture();
        var prior = f.Import("one.wav", 42);
        string destination = f.Publish().ResourceRoot;
        string index = Path.Combine(destination, "rukari-voices", "index.json");
        byte[] before = File.ReadAllBytes(index);
        string foreign = Path.Combine(destination, "notes.txt");
        File.WriteAllText(foreign, "leave me alone");
        f.Import("second.wav", 43);
        var missing = f.Import("third.wav", 44);
        File.Delete(Path.Combine(f.Project, missing.RelativePath));
        Throws(() => f.Publish());
        Check(before.SequenceEqual(File.ReadAllBytes(index)));
        Check(File.ReadAllText(foreign) == "leave me alone");
        Check(ProjectVoiceImportPolicy.TryResolvePublishedVoice(f.Playback, prior.ResourceKey, out _));
        Check(Directory.GetFiles(Path.Combine(destination, "rukari-voices"), "*.wav").Length == 1);
    }

    internal static void RepeatedPublishSkipsUnchangedBytesButDetectsChangedAudio()
    {
        using var f = new Fixture();
        var entry = f.Import("one.wav", 52);
        string root = f.Publish().ResourceRoot;
        Check(f.Publish().Unchanged);
        string published = Path.Combine(root, "rukari-voices", entry.Hash + ".wav");
        byte[] bytes = File.ReadAllBytes(published);
        bytes[30] ^= 255;
        File.WriteAllBytes(published, bytes);
        File.SetLastWriteTimeUtc(published, DateTime.UtcNow.AddSeconds(10));
        Throws(() => f.Publish());
        Check(!ProjectVoiceImportPolicy.TryResolvePublishedVoice(f.Playback, entry.ResourceKey, out _));
    }

    internal static void InvalidArchiveNamePathsAndCancellationDoNotPublish()
    {
        using var f = new Fixture();
        f.Import("one.wav", 62);
        Throws(() => ProjectVoiceImportPolicy.PublishForArchive(f.Project, "another", f.Archive, CancellationToken.None));
        Throws(() => ProjectVoiceImportPolicy.PublishForArchive(f.Project, "work", f.Archive + ".bak", CancellationToken.None));
        Throws(() => ProjectVoiceImportPolicy.PublishForArchive(f.Project, "work", Path.Combine(f.Project, "work.aas"), CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { ProjectVoiceImportPolicy.PublishForArchive(f.Project, "work", f.Archive, cancellation.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Check(!Directory.Exists(f.Playback + ".rukari"));
        Check(!ProjectVoiceImportPolicy.TryResolvePublishedVoice(f.Playback, "rukari-import/../../escape", out _));
    }

    internal static void ConcurrentImportRefusesPublicationWithoutBlocking()
    {
        using var f = new Fixture();
        f.Import("one.wav", 72);
        using var held = new FileStream(Path.Combine(f.Project, "rukari-voices", ".import.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Throws(() => f.Publish());
        Check(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static void Check(bool pass) { if (!pass) throw new Exception("Voice publication check failed."); }
    private static void Throws(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected refusal."); }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "rukari-voice-publication-tests-" + Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "projects", "work");
        internal string Playback => Path.Combine(Root, "saves", "work");
        internal string Archive => Playback + ".aas";
        internal Fixture()
        {
            Directory.CreateDirectory(Project);
            Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
            // Synthetic fixtures only: no game archive is read, interpreted or edited by these tests.
            File.WriteAllText(Archive, "fake archive supplied by test fixture");
            File.WriteAllText(Path.Combine(Project, "manifest.json"), "{\"VoiceOverrides\":[],\"keep\":true}");
        }
        internal ProjectVoiceImportEntry Import(string name, byte marker)
        {
            string source = Path.Combine(Root, "input"); Directory.CreateDirectory(source);
            string file = Path.Combine(source, name);
            var bytes = new byte[512]; bytes[20] = marker;
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
            File.WriteAllBytes(file, bytes);
            ProjectVoiceImportPolicy.ImportAsync(new(Project), source,
                new[] { new VoiceImportSource(file, name, bytes.Length, ".wav", File.GetLastWriteTimeUtc(file).Ticks) },
                0, CancellationToken.None).GetAwaiter().GetResult();
            return ProjectVoiceImportPolicy.ReadCatalog(Project).Single(item => item.DisplayName == name);
        }
        internal VoicePublicationResult Publish() => ProjectVoiceImportPolicy.PublishForArchive(Project, "work", Archive, CancellationToken.None);
        public void Dispose()
        {
            string actual = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(actual) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(actual).StartsWith("rukari-voice-publication-tests-", StringComparison.Ordinal))
                throw new IOException("Unexpected fixture cleanup path.");
            Directory.Delete(actual, recursive: true);
        }
    }
}
