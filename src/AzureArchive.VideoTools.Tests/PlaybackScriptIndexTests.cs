using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Guards the two optimizations that took a per-selection sweep from ~98 ms to
/// ~3 ms on the real corpus: the cached projected-script index, and taking file
/// metadata from one directory enumeration instead of one file system call per
/// file. Both are only allowed to make the same decisions faster, so most of
/// these tests compare the fast path against the behaviour it replaced.
/// </summary>
internal static class PlaybackScriptIndexTests
{
    private const string Key = "test-projection";

    public static void SharedDirectiveSnapshotsSeparateProjectionIndexes()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "namespaces.aap", "A");
        const string source = "#aavt;char;3;set;x=50\n#thirdparty;flash\n3;actor;01;A";
        PlaybackArchiveSnapshot playback = Playback(project, files, "namespaces.aas", Record(0, "A", source));
        using var service = new Rukari.Lib.Commands.EmbeddedDirectiveService(static () => true, static () => true, null);
        AssertEx.True(service.Register("effects", new[] { "#aavt" }, static _ => { }).Success);
        var first = service.CaptureSanitizer();
        var thirdParty = service.Register("thirdparty", new[] { "#thirdparty" }, static _ => { });
        AssertEx.True(thirdParty.Success);
        var second = service.CaptureSanitizer();
        AssertEx.False(first.Revision == second.Revision);
        AssertEx.Equal("#thirdparty;flash\n3;actor;01;A", first.Sanitize(source));
        AssertEx.Equal("3;actor;01;A", second.Sanitize(source));

        ObservedSceneIdentityResolver With(Rukari.Lib.Commands.IEmbeddedDirectiveSanitizer snapshot) =>
            new(playbackMapper: null, compiledScriptProjection: snapshot.Sanitize,
                projectionCacheKey: () => "test-shared-directives:" + snapshot.Revision);
        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(With(first).Resolve(Identity(first.Sanitize(source)), project, playback).Value).Status);
        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(With(second).Resolve(Identity(second.Sanitize(source)), project, playback).Value).Status);
        // Registration changes during a sweep cannot alter the snapshot used to fill its cache.
        thirdParty.Value.Dispose();
        AssertEx.Equal(ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(With(second).Resolve(Identity("3;actor;01;A"), project, playback).Value).Status);
        AssertEx.Equal(ObservedSceneIdentityStatus.NotFound,
            AssertEx.NotNull(With(first).Resolve(Identity("3;actor;01;A"), project, playback).Value).Status);
    }

    public static void IndexedResolutionMatchesTheRecordScanForEveryRecord()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "indexed.aap", "A", "B", "C");
        string[] scripts =
        {
            "3;actor;01;A\n#wait;500\n",
            "3;actor;01;B\n",
            "duplicate\n",
            "duplicate\n",
            "unmapped-script\n"
        };
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "indexed.aas",
            scripts.Select((script, index) =>
                Record(index, index < 3 ? $"{"ABC"[index]}" : "A", script)).ToArray());

        var indexed = new ObservedSceneIdentityResolver(
            playbackMapper: null,
            compiledScriptProjection: null,
            projectionCacheKey: () => Key);
        var scanned = new ObservedSceneIdentityResolver();

        foreach (string script in scripts.Append("absent-from-every-record"))
        {
            ObservedCompiledSceneIdentity identity = Identity(script);
            Result<ObservedSceneIdentityResolution> fast = indexed.Resolve(
                identity,
                project,
                playback);
            Result<ObservedSceneIdentityResolution> slow = scanned.Resolve(
                identity,
                project,
                playback);

            AssertEx.Equal(slow.Success, fast.Success, $"success differs for '{script}'");
            AssertEx.Equal(slow.Error, fast.Error, $"error differs for '{script}'");
            ObservedSceneIdentityResolution expected = AssertEx.NotNull(slow.Value);
            ObservedSceneIdentityResolution actual = AssertEx.NotNull(fast.Value);
            AssertEx.Equal(expected.Status, actual.Status, $"status differs for '{script}'");
            AssertEx.Equal(
                expected.PlaybackRecordIndex,
                actual.PlaybackRecordIndex,
                $"record index differs for '{script}'");
            AssertEx.Equal(
                string.Join(",", expected.CandidatePlaybackRecordIndices),
                string.Join(",", actual.CandidatePlaybackRecordIndices),
                $"candidates differ for '{script}'");
            AssertEx.Equal(
                expected.SelectedSceneTrusted,
                actual.SelectedSceneTrusted,
                $"selected trust differs for '{script}'");
            AssertEx.Equal(
                expected.Scene?.NodeGuid,
                actual.Scene?.NodeGuid,
                $"scene differs for '{script}'");
        }
    }

    /// <summary>
    /// The reason the brute-force scan existed: an archive that changed on disk
    /// must be seen immediately. An index is bound to one snapshot instance, so
    /// a re-read archive can never be answered from the previous index.
    /// </summary>
    public static void EditedArchiveIsNeverServedByAStaleIndex()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "edited.aap", "A", "B");
        const string originalScript = "3;actor;01;A\n";
        const string editedScript = "3;actor;01;A-edited\n";
        var resolver = new ObservedSceneIdentityResolver(
            playbackMapper: null,
            compiledScriptProjection: null,
            projectionCacheKey: () => Key);

        PlaybackArchiveSnapshot before = Playback(
            project,
            files,
            "edited.aas",
            Record(0, "A", originalScript));
        AssertEx.Equal(
            ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(resolver.Resolve(Identity(originalScript), project, before).Value).Status);

        // Same paths, same resolver, same cache key: only the archive content
        // changed, exactly as if the user re-exported the project.
        PlaybackArchiveSnapshot after = Playback(
            project,
            files,
            "edited.aas",
            Record(0, "A", editedScript));

        AssertEx.Equal(
            ObservedSceneIdentityStatus.NotFound,
            AssertEx.NotNull(resolver.Resolve(Identity(originalScript), project, after).Value).Status,
            "the previous archive's script must not resolve against the re-read archive");
        AssertEx.Equal(
            ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(resolver.Resolve(Identity(editedScript), project, after).Value).Status,
            "the edited archive's script must resolve immediately");
    }

    public static void ProjectionKeyIsReadLiveOnEveryResolution()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "live.aap", "A");
        const string raw = "3;actor;01;A\n#aavt;char;3;move;dx=500;duration=800;easing=easeInOut\n";
        const string sanitized = "3;actor;01;A\n";
        PlaybackArchiveSnapshot playback = Playback(
            project,
            files,
            "live.aas",
            Record(0, "A", raw));
        var extractor = new EmbeddedAavtDirectiveExtractor();
        string key = "identity";
        var resolver = new ObservedSceneIdentityResolver(
            playbackMapper: null,
            compiledScriptProjection: script => key == "identity"
                ? script
                : extractor.Extract(script, true).SanitizedText,
            projectionCacheKey: () => key);

        // With the identity projection the raw script is the identity.
        AssertEx.Equal(
            ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(resolver.Resolve(Identity(raw), project, playback).Value).Status);

        // Flipping the key must invalidate the cached index rather than let the
        // old projection keep answering for the new one.
        key = "embedded";
        AssertEx.Equal(
            ObservedSceneIdentityStatus.NotFound,
            AssertEx.NotNull(resolver.Resolve(Identity(raw), project, playback).Value).Status,
            "the identity-projection index must not answer for the embedded projection");
        AssertEx.Equal(
            ObservedSceneIdentityStatus.Mapped,
            AssertEx.NotNull(resolver.Resolve(Identity(sanitized), project, playback).Value).Status);
    }

    public static void IndexedResolutionReportsContractViolationsIdentically()
    {
        using var files = new TemporaryAapDirectory();
        ProjectSnapshot project = ReadProject(files, "contract.aap", "A");
        PlaybackArchiveSnapshot positional = Playback(
            project,
            files,
            "contract.aas",
            Record(4, "A", "script"));
        PlaybackArchiveSnapshot nullRecord = new(
            positional.Source,
            "synthetic",
            Array.AsReadOnly(new PlaybackRecordSnapshot?[] { null })!);

        var indexed = new ObservedSceneIdentityResolver(
            playbackMapper: null,
            compiledScriptProjection: null,
            projectionCacheKey: () => Key);
        var scanned = new ObservedSceneIdentityResolver();
        ObservedCompiledSceneIdentity identity = Identity("script");

        Result<ObservedSceneIdentityResolution> fastPositional =
            indexed.Resolve(identity, project, positional);
        Result<ObservedSceneIdentityResolution> slowPositional =
            scanned.Resolve(identity, project, positional);
        AssertEx.False(fastPositional.Success);
        AssertEx.Equal(slowPositional.Error, fastPositional.Error);

        Result<ObservedSceneIdentityResolution> fastNull =
            indexed.Resolve(identity, project, nullRecord);
        Result<ObservedSceneIdentityResolution> slowNull =
            scanned.Resolve(identity, project, nullRecord);
        AssertEx.False(fastNull.Success);
        AssertEx.Equal(slowNull.Error, fastNull.Error);
    }

    /// <summary>
    /// A stamp set is authoritative: the cache must trust matching stamps
    /// without touching the file, and must treat a differing stamp as a change.
    /// This is the mechanism that removed ~228 file system calls per sweep, so
    /// it is verified against a file that really changed underneath.
    /// </summary>
    public static void StampSetDecidesCacheValidationInsteadOfTheFileSystem()
    {
        using var files = new TemporaryAapDirectory();
        string path = files.Write("stamped.aap", SyntheticAap.Project(
            new[] { SyntheticAap.Scene("A") }));
        DateTime originalWriteTime = new FileInfo(path).LastWriteTimeUtc;
        long originalLength = new FileInfo(path).Length;
        var cache = new ArchiveSnapshotCache();
        int reads = 0;
        Func<string, Result<ProjectSnapshot>> reader = p =>
        {
            reads++;
            return new AapProjectReader().Read(p);
        };

        DataRootStampSet matching = DataRootStampSet.Capture(files.Root);
        SceneKey snapshotKey = AssertEx.NotNull(
            cache.GetOrReadProject(path, matching, reader).Value)
            .ScriptNodes.Single().Scenes[0].Key;
        AssertEx.Equal(1, reads);

        // Replace the content with an equally long project naming a different
        // scene, then restore the write time: the file is different, but both
        // halves of the cached key still agree. Only a stamp-driven cache can
        // serve the old snapshot here, which is what proves it never asked.
        string replacement = SyntheticAap.Project(new[] { SyntheticAap.Scene("B") });
        AssertEx.Equal(
            originalLength,
            (long)Encoding.UTF8.GetByteCount(replacement),
            "the replacement fixture must keep the same length to hold the key fixed");
        files.Write("stamped.aap", replacement);
        File.SetLastWriteTimeUtc(path, originalWriteTime);

        SceneKey servedFromStamps = AssertEx.NotNull(
            cache.GetOrReadProject(path, matching, reader).Value)
            .ScriptNodes.Single().Scenes[0].Key;
        AssertEx.Equal(snapshotKey, servedFromStamps, "matching stamps must not re-read");
        AssertEx.Equal(1, reads);

        // Move the write time forward: the stamps now describe a different
        // version, so the same cache must re-read and report the new content.
        File.SetLastWriteTimeUtc(path, originalWriteTime.AddMinutes(1));
        DataRootStampSet changed = DataRootStampSet.Capture(files.Root);
        SceneKey rereadKey = AssertEx.NotNull(
            cache.GetOrReadProject(path, changed, reader).Value)
            .ScriptNodes.Single().Scenes[0].Key;
        AssertEx.Equal(2, reads, "a differing stamp must invalidate the cached snapshot");
        AssertEx.True(
            !snapshotKey.Equals(rereadKey),
            "the re-read snapshot must describe the replaced content");
    }

    public static void MissingAndUnstampedPathsStillFallBackToTheFileSystem()
    {
        using var files = new TemporaryAapDirectory();
        string root = Path.Combine(files.Root, "data");
        string projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(projects);
        string present = Path.Combine(projects, "present.aap");
        File.WriteAllText(present, SyntheticAap.Project(new[] { SyntheticAap.Scene("A") }));
        string absent = Path.Combine(projects, "absent.aap");
        string outside = Path.Combine(files.Root, "outside.aap");
        File.WriteAllText(outside, SyntheticAap.Project(new[] { SyntheticAap.Scene("A") }));
        DataRootStampSet stamps = DataRootStampSet.Capture(root);

        var cache = new ArchiveSnapshotCache();
        AssertEx.True(stamps.TryGet(present, out _), "the fixture file must be stamped");
        AssertEx.True(cache.GetOrReadProject(
            present,
            stamps,
            p => new AapProjectReader().Read(p)).Success);

        AssertEx.True(cache.GetOrReadProject(
            outside,
            stamps,
            p => new AapProjectReader().Read(p)).Success,
            "an unstamped but present file must still be read from disk");

        Result<ProjectSnapshot> missing = cache.GetOrReadProject(
            absent,
            stamps,
            p => new AapProjectReader().Read(p));
        AssertEx.False(missing.Success);
        AssertEx.True(
            missing.Error.Contains("invalid", StringComparison.OrdinalIgnoreCase),
            $"unexpected missing-file error: {missing.Error}");
    }

    public static void StampCaptureDescribesADataRootAndIgnoresUnrelatedPaths()
    {
        using var files = new TemporaryAapDirectory();
        string root = Path.Combine(files.Root, "data");
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Directory.CreateDirectory(Path.Combine(root, "saves"));
        string project = Path.Combine(root, "projects", "stamped.aap");
        string playback = Path.Combine(root, "saves", "stamped.aas");
        File.WriteAllText(project, SyntheticAap.Project(new[] { SyntheticAap.Scene("A") }));
        File.WriteAllBytes(playback, new byte[] { 1, 2, 3 });

        DataRootStampSet stamps = DataRootStampSet.Capture(root);

        AssertEx.Equal(2, stamps.Count);
        AssertEx.True(stamps.TryGet(project, out FileStamp projectStamp));
        AssertEx.Equal(new FileInfo(project).LastWriteTimeUtc, projectStamp.LastWriteTimeUtc);
        AssertEx.Equal(new FileInfo(project).Length, projectStamp.Length);
        AssertEx.True(stamps.TryGet(playback, out FileStamp playbackStamp));
        AssertEx.Equal(3L, playbackStamp.Length);
        AssertEx.False(
            stamps.TryGet(Path.Combine(files.Root, "elsewhere.aap"), out _),
            "a path outside the data root must be reported as unknown");
        AssertEx.Equal(0, DataRootStampSet.Capture(null).Count);
        AssertEx.Equal(0, DataRootStampSet.Capture(Path.Combine(files.Root, "nope")).Count);
    }

    public static void ScannerUsesStampsForPlaybackPresence()
    {
        using var files = new TemporaryAapDirectory();
        string root = Path.Combine(files.Root, "data");
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Directory.CreateDirectory(Path.Combine(root, "saves"));
        File.WriteAllText(
            Path.Combine(root, "projects", "alpha.aap"),
            SyntheticAap.Project(new[] { SyntheticAap.Scene("alpha") }));

        // The playback file exists on disk, but the caller's stamps were taken
        // before it: presence follows the stamps, which is what makes one
        // directory enumeration replace ~114 existence checks.
        File.WriteAllBytes(Path.Combine(root, "saves", "alpha.aas"), new byte[] { 1 });
        DataRootStampSet beforeWrite = DataRootStampSet.Capture(root);
        File.Delete(Path.Combine(root, "saves", "alpha.aas"));

        IReadOnlyList<DiscoveredProjectPair> stamped = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root, beforeWrite).Value);
        AssertEx.Equal(1, stamped.Count);
        AssertEx.True(
            stamped[0].PlaybackPresent,
            "the stamps must decide presence for a path they describe");

        // A path the stamps do not describe falls back to the file system.
        IReadOnlyList<DiscoveredProjectPair> unstamped = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root, DataRootStampSet.Empty).Value);
        AssertEx.Equal(1, unstamped.Count);
        AssertEx.False(
            unstamped[0].PlaybackPresent,
            "without stamps the scanner must ask the file system");
    }

    /// <summary>
    /// Project archives dominate a first sweep, so a pair whose playback cannot
    /// carry the script must never have its project archive parsed. Only the
    /// pair that really matches should cost a JSON parse.
    /// </summary>
    public static void ArbitrationReadsOnlyTheMatchingProjectArchive()
    {
        using var files = new TemporaryAapDirectory();
        string root = DirectoryRoot(files);
        const string script = "3;actor;01;target\n";
        string[] names = { "alpha", "beta", "gamma", "delta", "epsilon" };
        for (int index = 0; index < names.Length; index++)
        {
            WritePair(
                root,
                names[index],
                index == 2 ? new[] { script } : new[] { $"3;actor;01;{names[index]}\n" });
        }

        IReadOnlyList<DiscoveredProjectPair> pairs = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root).Value);
        AssertEx.Equal(names.Length, pairs.Count);

        int projectReads = 0;
        int playbackReads = 0;
        var resolver = new ObservedSceneIdentityResolver(
            playbackMapper: null,
            compiledScriptProjection: null,
            projectionCacheKey: () => Key);
        ActivePairArbitration indexed = new ActiveProjectPairArbiter(
            path =>
            {
                projectReads++;
                return new AapProjectReader().Read(path);
            },
            path =>
            {
                playbackReads++;
                return new AasScenarioReader().Read(path);
            },
            resolver).Arbitrate(Identity(script), pairs);

        AssertEx.True(indexed.Success, string.Join("|", indexed.Diagnostics));
        AssertEx.Equal("gamma", AssertEx.NotNull(indexed.Pair).Name);
        AssertEx.Equal(names.Length, playbackReads);
        AssertEx.Equal(1, projectReads);

        // Without an index the pre-filter abstains, so the old both-archives
        // behaviour has to remain available and correct.
        int unfilteredProjectReads = 0;
        ActivePairArbitration unfiltered = new ActiveProjectPairArbiter(
            path =>
            {
                unfilteredProjectReads++;
                return new AapProjectReader().Read(path);
            },
            path => new AasScenarioReader().Read(path),
            new ObservedSceneIdentityResolver()).Arbitrate(Identity(script), pairs);

        AssertEx.True(unfiltered.Success, string.Join("|", unfiltered.Diagnostics));
        AssertEx.Equal("gamma", AssertEx.NotNull(unfiltered.Pair).Name);
        AssertEx.Equal(names.Length, unfilteredProjectReads);
    }

    public static void ArbitrationDecisionsMatchWithAndWithoutThePreFilter()
    {
        using var files = new TemporaryAapDirectory();
        string root = DirectoryRoot(files);
        const string shared = "3;actor;01;shared\n";
        WritePair(root, "alpha", new[] { shared });
        WritePair(root, "beta", new[] { shared });
        WritePair(root, "gamma", new[] { "3;actor;01;unique\n" });
        WritePair(root, "delta", Array.Empty<string>());

        IReadOnlyList<DiscoveredProjectPair> pairs = AssertEx.NotNull(
            new ProjectPairScanner().Scan(root).Value);

        foreach (string script in new[] { shared, "3;actor;01;unique\n", "3;actor;01;absent\n" })
        {
            ActivePairArbitration filtered = new ActiveProjectPairArbiter(
                path => new AapProjectReader().Read(path),
                path => new AasScenarioReader().Read(path),
                new ObservedSceneIdentityResolver(
                    playbackMapper: null,
                    compiledScriptProjection: null,
                    projectionCacheKey: () => Key)).Arbitrate(Identity(script), pairs);
            ActivePairArbitration plain = new ActiveProjectPairArbiter(
                path => new AapProjectReader().Read(path),
                path => new AasScenarioReader().Read(path),
                new ObservedSceneIdentityResolver()).Arbitrate(Identity(script), pairs);

            AssertEx.Equal(plain.Status, filtered.Status, $"status differs for '{script}'");
            AssertEx.Equal(
                plain.Pair?.AapPath ?? "none",
                filtered.Pair?.AapPath ?? "none",
                $"pair differs for '{script}'");
            AssertEx.Equal(
                plain.Resolution?.PlaybackRecordIndex,
                filtered.Resolution?.PlaybackRecordIndex,
                $"record differs for '{script}'");
            AssertEx.Equal(
                plain.ScannedPairCount,
                filtered.ScannedPairCount,
                $"scanned count differs for '{script}'");
            AssertEx.Equal(
                plain.Diagnostics.Count,
                filtered.Diagnostics.Count,
                $"diagnostic count differs for '{script}': "
                + $"[{string.Join(" | ", plain.Diagnostics)}] vs "
                + $"[{string.Join(" | ", filtered.Diagnostics)}]");
        }
    }

    private static string DirectoryRoot(TemporaryAapDirectory files)
    {
        string root = Path.Combine(files.Root, "data");
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Directory.CreateDirectory(Path.Combine(root, "saves"));
        return root;
    }

    private static void WritePair(string root, string name, string[] compiledScripts)
    {
        File.WriteAllText(
            Path.Combine(root, "projects", $"{name}.aap"),
            SyntheticAap.Project(new[] { SyntheticAap.Scene(name) }));
        if (compiledScripts.Length == 0)
        {
            return;
        }

        // The dialogue texts have to name the scene, otherwise the mapper
        // reports PlaybackRecordOnly instead of binding the record to it.
        File.WriteAllBytes(
            Path.Combine(root, "saves", $"{name}.aas"),
            SyntheticAas.Archive(new SyntheticAasRecord(
                CompiledScript: compiledScripts[0],
                TextJp: name,
                TextTh: name,
                TextTw: name,
                TextCn: name,
                TextEn: name)));
    }

    private static ObservedCompiledSceneIdentity Identity(string compiledScript)
    {
        string sha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(compiledScript)));
        return new ObservedCompiledSceneIdentity(
            sha256,
            compiledScript.Length,
            compiledScript.Count(character => character == '\n') + 1);
    }

    private static ProjectSnapshot ReadProject(
        TemporaryAapDirectory files,
        string fileName,
        params string[] dialogue)
    {
        string path = files.Write(
            fileName,
            SyntheticAap.Project(
                dialogue.Select(text => SyntheticAap.Scene(text)).ToArray()));
        Result<ProjectSnapshot> result = new AapProjectReader().Read(path);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static PlaybackArchiveSnapshot Playback(
        ProjectSnapshot project,
        TemporaryAapDirectory files,
        string fileName,
        params PlaybackRecordSnapshot[] records)
    {
        string fullPath = Path.Combine(files.Root, fileName);
        var source = new PlaybackArchiveSourceSnapshot(
            fullPath,
            new string('1', 64),
            new string('2', 64),
            1,
            project.Source.LastWriteTimeUtc);
        return new PlaybackArchiveSnapshot(
            source,
            "synthetic",
            Array.AsReadOnly(records));
    }

    private static PlaybackRecordSnapshot Record(
        int index,
        string text,
        string compiledScript) => new(
        index,
        123,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        compiledScript,
        text,
        text,
        text,
        string.Empty,
        text,
        string.Empty,
        true,
        new string('A', 64));
}
