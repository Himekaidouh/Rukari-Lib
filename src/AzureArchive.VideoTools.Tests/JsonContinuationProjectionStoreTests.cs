using AzureArchive.VideoTools.Core.Compilation;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Sidecar;

namespace AzureArchive.VideoTools.Tests;

internal static class JsonContinuationProjectionStoreTests
{
    private const string RelativePath = "projects/test.aavt.playback.json";

    public static void RoundTripsAndRotatesValidatedBackup()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "roundtrip.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection first = Compile(fixture, 1);
        ContinuationPlaybackProjection second = Compile(fixture, 1, 2);
        ContinuationPlaybackProjection third = Compile(fixture, 2);

        ContinuationProjectionSaveSnapshot firstSave = AssertEx.NotNull(
            store.SaveAtomic(
                RelativePath,
                first,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.False(firstSave.ReplacedExisting);
        AssertEx.False(firstSave.BackupCreated);

        AssertEx.True(
            store.SaveAtomic(
                RelativePath,
                second,
                fixture.Project,
                fixture.Playback).Success);
        ContinuationProjectionSaveSnapshot thirdSave = AssertEx.NotNull(
            store.SaveAtomic(
                RelativePath,
                third,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.True(thirdSave.ReplacedExisting);
        AssertEx.True(thirdSave.BackupCreated);

        ContinuationProjectionLoadSnapshot primary = Load(store, fixture);
        AssertEx.Equal(ContinuationProjectionLoadSource.Primary, primary.Source);
        AssertProjectionEqual(third, AssertEx.NotNull(primary.Projection));

        File.WriteAllText(thirdSave.FullPath, "{ broken-json }");
        ContinuationProjectionLoadSnapshot recovered = Load(store, fixture);
        AssertEx.True(recovered.RecoveredFromBackup);
        AssertProjectionEqual(second, AssertEx.NotNull(recovered.Projection));
    }

    public static void RejectsStaleAapAndAasIdentities()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "stale-identities.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection projection = Compile(fixture, 1);
        AssertEx.True(store.SaveAtomic(
            RelativePath,
            projection,
            fixture.Project,
            fixture.Playback).Success);

        ProjectSnapshot changedProject = fixture.Project with
        {
            Source = fixture.Project.Source with
            {
                RevisionSha256 = new string('D', 64)
            }
        };
        var staleProject = store.TryLoad(
            RelativePath,
            changedProject,
            fixture.Playback);
        AssertEx.False(staleProject.Success);
        AssertEx.True(
            staleProject.Error.Contains("current AAP", StringComparison.Ordinal),
            staleProject.Error);

        PlaybackArchiveSnapshot changedPlayback = fixture.Playback with
        {
            Source = fixture.Playback.Source with
            {
                RevisionSha256 = new string('E', 64)
            }
        };
        var stalePlayback = store.TryLoad(
            RelativePath,
            fixture.Project,
            changedPlayback);
        AssertEx.False(stalePlayback.Success);
        AssertEx.True(
            stalePlayback.Error.Contains("current AAS", StringComparison.Ordinal),
            stalePlayback.Error);
    }

    public static void RejectsCurrentRecordFingerprintDrift()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "fingerprint-drift.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection projection = Compile(fixture, 1);
        AssertEx.True(store.SaveAtomic(
            RelativePath,
            projection,
            fixture.Project,
            fixture.Playback).Success);

        PlaybackRecordSnapshot[] records = fixture.Playback.Records.ToArray();
        records[1] = records[1] with { Fingerprint = new string('F', 64) };
        PlaybackArchiveSnapshot changedPlayback = fixture.Playback with
        {
            Records = Array.AsReadOnly(records)
        };

        var load = store.TryLoad(RelativePath, fixture.Project, changedPlayback);
        AssertEx.False(load.Success);
        AssertEx.True(
            load.Error.Contains("fingerprint", StringComparison.OrdinalIgnoreCase),
            load.Error);
    }

    public static void LoadsBackupOnlyWhenItMatchesCurrentSnapshots()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture original = CreateFixture(files, "revision-backup.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection originalProjection = Compile(original, 1);
        AssertEx.True(store.SaveAtomic(
            RelativePath,
            originalProjection,
            original.Project,
            original.Playback).Success);

        PlaybackArchiveSnapshot newerPlayback = original.Playback with
        {
            Source = original.Playback.Source with
            {
                RevisionSha256 = new string('D', 64)
            }
        };
        var newer = original with { Playback = newerPlayback };
        ContinuationPlaybackProjection newerProjection = Compile(newer, 1, 2);
        AssertEx.True(store.SaveAtomic(
            RelativePath,
            newerProjection,
            newer.Project,
            newer.Playback).Success);

        ContinuationProjectionLoadSnapshot recovered = Load(store, original);
        AssertEx.Equal(ContinuationProjectionLoadSource.Backup, recovered.Source);
        AssertProjectionEqual(
            originalProjection,
            AssertEx.NotNull(recovered.Projection));
        AssertEx.Contains(
            recovered.Notices,
            notice => notice.Code == "PrimaryRejected",
            "Expected an explicit primary-rejected recovery notice.");
    }

    public static void RejectsIncompleteIntermediateAppendChain()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "incomplete-chain.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection complete = Compile(fixture, 1, 2);
        ContinuationPlaybackProjection incomplete = complete with
        {
            Instructions = Array.AsReadOnly(new[] { complete.Instructions[1] })
        };

        var save = store.SaveAtomic(
            RelativePath,
            incomplete,
            fixture.Project,
            fixture.Playback);
        AssertEx.False(save.Success);
        AssertEx.True(
            save.Error.Contains("incomplete intermediate append chain", StringComparison.Ordinal),
            save.Error);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void RejectsTamperedDialogueSemanticsBeforeWriting()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "tampered-dialogue.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection valid = Compile(fixture, 1);
        ContinuationPlaybackInstruction changedInstruction = valid.Instructions[0] with
        {
            ExpectedLockedPrefix = "X",
            ResultText = "XB",
            TypewriterStartCharacterIndex = 1
        };
        ContinuationPlaybackProjection tampered = valid with
        {
            Instructions = Array.AsReadOnly(new[] { changedInstruction })
        };

        var save = store.SaveAtomic(
            RelativePath,
            tampered,
            fixture.Project,
            fixture.Playback);
        AssertEx.False(save.Success);
        AssertEx.True(
            save.Error.Contains("dialogue text", StringComparison.OrdinalIgnoreCase),
            save.Error);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void RejectsHandcraftedAmbiguousTextMapping()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "ambiguous-handcraft.aap");
        PlaybackRecordSnapshot[] duplicateRecords = new[] { "A", "B", "A", "B" }
            .Select((text, index) => Record(index, text))
            .ToArray();
        PlaybackArchiveSnapshot ambiguousPlayback = fixture.Playback with
        {
            Source = fixture.Playback.Source with
            {
                Length = duplicateRecords.Length,
                RevisionSha256 = new string('E', 64)
            },
            Records = Array.AsReadOnly(duplicateRecords)
        };
        SceneSnapshot[] scenes = fixture.Project.ScriptNodes.Single().Scenes.ToArray();
        var instruction = new ContinuationPlaybackInstruction(
            scenes[1].Key,
            scenes[0].Key,
            scenes[0].Key,
            1,
            duplicateRecords[1].Fingerprint,
            0,
            0,
            "A",
            "B",
            "AB",
            1);
        var handcrafted = new ContinuationPlaybackProjection(
            ContinuationPlaybackProjection.CurrentSchemaVersion,
            "d673b794-354d-4ea7-a4f7-cb885b6020e8",
            fixture.Project.Source.PathKey,
            fixture.Project.Source.RevisionSha256,
            ambiguousPlayback.Source.PathKey,
            ambiguousPlayback.Source.RevisionSha256,
            ambiguousPlayback.SchemaName,
            Array.AsReadOnly(new[] { instruction }));
        var store = CreateStore(files);

        var save = store.SaveAtomic(
            RelativePath,
            handcrafted,
            fixture.Project,
            ambiguousPlayback);
        AssertEx.False(save.Success);
        AssertEx.True(
            save.Error.Contains("trusted compiler", StringComparison.Ordinal),
            save.Error);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void RejectsTraversalOfficialAndSourceSidecarExtensions()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "paths.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection projection = Compile(fixture, 1);

        AssertEx.False(store.SaveAtomic(
            "../escape.aavt.playback.json",
            projection,
            fixture.Project,
            fixture.Playback).Success);
        AssertEx.False(store.SaveAtomic(
            "project.aap",
            projection,
            fixture.Project,
            fixture.Playback).Success);
        AssertEx.False(store.SaveAtomic(
            "save.aas",
            projection,
            fixture.Project,
            fixture.Playback).Success);
        AssertEx.False(store.SaveAtomic(
            "source.aavt.json",
            projection,
            fixture.Project,
            fixture.Playback).Success);
        AssertEx.False(store.SaveAtomic(
            Path.Combine(files.Root, "absolute.aavt.playback.json"),
            projection,
            fixture.Project,
            fixture.Playback).Success);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void RejectsUnknownAndDuplicateJsonProperties()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "strict-json.aap");
        var store = CreateStore(files);
        ContinuationProjectionSaveSnapshot saved = AssertEx.NotNull(
            store.SaveAtomic(
                RelativePath,
                Compile(fixture, 1),
                fixture.Project,
                fixture.Playback).Value);
        string original = File.ReadAllText(saved.FullPath);

        File.WriteAllText(
            saved.FullPath,
            original.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"unexpected\": true,",
                StringComparison.Ordinal));
        var unknown = store.TryLoad(
            RelativePath,
            fixture.Project,
            fixture.Playback);
        AssertEx.False(unknown.Success);
        AssertEx.True(
            unknown.Error.Contains("Unknown JSON property", StringComparison.Ordinal),
            unknown.Error);

        File.WriteAllText(
            saved.FullPath,
            original.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
                StringComparison.Ordinal));
        var duplicate = store.TryLoad(
            RelativePath,
            fixture.Project,
            fixture.Playback);
        AssertEx.False(duplicate.Success);
        AssertEx.True(
            duplicate.Error.Contains("Duplicate JSON property", StringComparison.Ordinal),
            duplicate.Error);
    }

    public static void MissingProjectionReturnsNoneWithoutCreatingRoot()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "missing.aap");
        var store = CreateStore(files);

        ContinuationProjectionLoadSnapshot load = Load(store, fixture);
        AssertEx.Equal(ContinuationProjectionLoadSource.None, load.Source);
        AssertEx.True(load.Projection == null);
        AssertEx.Equal(0, load.Notices.Count);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void ContendingWriterCannotReplaceProjection()
    {
        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "contending.aap");
        var firstStore = CreateStore(files);
        ContinuationPlaybackProjection first = Compile(fixture, 1);
        ContinuationPlaybackProjection second = Compile(fixture, 1, 2);
        ContinuationProjectionSaveSnapshot initial = AssertEx.NotNull(
            firstStore.SaveAtomic(
                RelativePath,
                first,
                fixture.Project,
                fixture.Playback).Value);

        using (new FileStream(
                   initial.FullPath + ".lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var contendingStore = new JsonContinuationProjectionStore(
                firstStore.RootDirectory,
                new JsonContinuationProjectionStoreOptions(
                    LockRetryAttempts: 2,
                    LockRetryDelayMilliseconds: 0));
            var save = contendingStore.SaveAtomic(
                RelativePath,
                second,
                fixture.Project,
                fixture.Playback);
            AssertEx.False(save.Success);
            AssertEx.True(
                save.Error.Contains("lock", StringComparison.OrdinalIgnoreCase),
                save.Error);
        }

        ContinuationProjectionLoadSnapshot loaded = Load(firstStore, fixture);
        AssertProjectionEqual(first, AssertEx.NotNull(loaded.Projection));
    }

    public static void FailedReplacementLeavesProjectionUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new TemporaryAapDirectory();
        ProjectionFixture fixture = CreateFixture(files, "blocked-replace.aap");
        var store = CreateStore(files);
        ContinuationPlaybackProjection first = Compile(fixture, 1);
        ContinuationPlaybackProjection second = Compile(fixture, 1, 2);
        ContinuationProjectionSaveSnapshot initial = AssertEx.NotNull(
            store.SaveAtomic(
                RelativePath,
                first,
                fixture.Project,
                fixture.Playback).Value);

        using (new FileStream(
                   initial.FullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var blocked = store.SaveAtomic(
                RelativePath,
                second,
                fixture.Project,
                fixture.Playback);
            AssertEx.False(blocked.Success);
        }

        ContinuationProjectionLoadSnapshot loaded = Load(store, fixture);
        AssertProjectionEqual(first, AssertEx.NotNull(loaded.Projection));
        string directory = AssertEx.NotNull(Path.GetDirectoryName(initial.FullPath));
        AssertEx.Equal(
            0,
            Directory.GetFiles(directory, "*.tmp.*", SearchOption.TopDirectoryOnly).Length);
    }

    private static JsonContinuationProjectionStore CreateStore(
        TemporaryAapDirectory files) =>
        new(Path.Combine(files.Root, "projection-sidecars"));

    private static ContinuationProjectionLoadSnapshot Load(
        JsonContinuationProjectionStore store,
        ProjectionFixture fixture)
    {
        Result<ContinuationProjectionLoadSnapshot> result = store.TryLoad(
            RelativePath,
            fixture.Project,
            fixture.Playback);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static ProjectionFixture CreateFixture(
        TemporaryAapDirectory files,
        string fileName)
    {
        string path = files.Write(
            fileName,
            SyntheticAap.Project(new[]
            {
                SyntheticAap.Scene("A"),
                SyntheticAap.Scene("B"),
                SyntheticAap.Scene("C")
            }));
        var projectResult = new AapProjectReader().Read(path);
        AssertEx.True(projectResult.Success, projectResult.Error);
        ProjectSnapshot project = AssertEx.NotNull(projectResult.Value);

        string playbackPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
        var playbackSource = new PlaybackArchiveSourceSnapshot(
            playbackPath,
            new string('B', 64),
            new string('C', 64),
            3,
            project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
        PlaybackRecordSnapshot[] records = new[] { "A", "B", "C" }
            .Select((text, index) => Record(index, text))
            .ToArray();
        var playback = new PlaybackArchiveSnapshot(
            playbackSource,
            "synthetic/v1",
            Array.AsReadOnly(records));
        return new ProjectionFixture(project, playback);
    }

    private static ContinuationPlaybackProjection Compile(
        ProjectionFixture fixture,
        params int[] continuedSceneIndices)
    {
        SceneSnapshot[] scenes = fixture.Project.ScriptNodes.Single().Scenes.ToArray();
        ContinuationRule[] rules = continuedSceneIndices
            .Select(index => new ContinuationRule(scenes[index].Key))
            .ToArray();
        var document = new ContinuationDocument(
            ContinuationDocument.CurrentSchemaVersion,
            "d673b794-354d-4ea7-a4f7-cb885b6020e8",
            fixture.Project.Source.PathKey,
            fixture.Project.Source.RevisionSha256,
            Array.AsReadOnly(rules));
        var result = new ContinuationProjectionCompiler().Compile(
            fixture.Project,
            fixture.Playback,
            document);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static PlaybackRecordSnapshot Record(int index, string text) => new(
        index,
        0,
        0,
        0,
        string.Empty,
        0,
        0,
        0,
        string.Empty,
        string.Empty,
        text,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        index.ToString("X64"));

    private static void AssertProjectionEqual(
        ContinuationPlaybackProjection expected,
        ContinuationPlaybackProjection actual)
    {
        AssertEx.Equal(expected.SchemaVersion, actual.SchemaVersion);
        AssertEx.Equal(expected.ProjectId, actual.ProjectId);
        AssertEx.Equal(expected.ProjectPathKey, actual.ProjectPathKey);
        AssertEx.Equal(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256);
        AssertEx.Equal(expected.PlaybackPathKey, actual.PlaybackPathKey);
        AssertEx.Equal(expected.PlaybackRevisionSha256, actual.PlaybackRevisionSha256);
        AssertEx.Equal(expected.PlaybackSchemaName, actual.PlaybackSchemaName);
        AssertEx.Equal(expected.Instructions.Count, actual.Instructions.Count);
        for (int index = 0; index < expected.Instructions.Count; index++)
        {
            AssertEx.Equal(expected.Instructions[index], actual.Instructions[index]);
        }
    }

    private sealed record ProjectionFixture(
        ProjectSnapshot Project,
        PlaybackArchiveSnapshot Playback);
}
