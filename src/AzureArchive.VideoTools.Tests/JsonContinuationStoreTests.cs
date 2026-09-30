using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Formats.Sidecar;

namespace AzureArchive.VideoTools.Tests;

internal static class JsonContinuationStoreTests
{
    private const string ProjectId = "d673b794-354d-4ea7-a4f7-cb885b6020e8";
    private const string NodeGuid = "6fcb9e8d-a1e2-4bb4-a610-b59795bd1036";
    private const string RelativePath = "projects/test.continuation.aavt.json";

    public static void RoundTripsAndRotatesAtomicBackup()
    {
        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);
        ContinuationDocument first = Document('A');
        ContinuationDocument second = Document('B');
        ContinuationDocument third = Document('C');

        Result<ContinuationSaveSnapshot> firstSave = store.SaveAtomic(RelativePath, first);
        AssertEx.True(firstSave.Success, firstSave.Error);
        ContinuationSaveSnapshot firstSnapshot = AssertEx.NotNull(firstSave.Value);
        AssertEx.False(firstSnapshot.ReplacedExisting);
        AssertEx.False(firstSnapshot.BackupCreated);

        Result<ContinuationSaveSnapshot> secondSave = store.SaveAtomic(RelativePath, second);
        AssertEx.True(secondSave.Success, secondSave.Error);
        AssertEx.True(AssertEx.NotNull(secondSave.Value).BackupCreated);

        Result<ContinuationSaveSnapshot> thirdSave = store.SaveAtomic(RelativePath, third);
        AssertEx.True(thirdSave.Success, thirdSave.Error);
        ContinuationSaveSnapshot thirdSnapshot = AssertEx.NotNull(thirdSave.Value);
        AssertEx.True(thirdSnapshot.ReplacedExisting);
        AssertEx.True(thirdSnapshot.BackupCreated);

        ContinuationLoadSnapshot primary = Load(store, RelativePath);
        AssertEx.Equal(ContinuationLoadSource.Primary, primary.Source);
        AssertDocumentEqual(third, AssertEx.NotNull(primary.Document));

        string backupPath = thirdSnapshot.FullPath + JsonContinuationStore.BackupSuffix;
        AssertEx.True(File.Exists(backupPath));
        var backupStore = CreateStore(files);
        File.WriteAllText(thirdSnapshot.FullPath, "{ broken-json }");
        ContinuationLoadSnapshot recovered = Load(backupStore, RelativePath);
        AssertEx.Equal(ContinuationLoadSource.Backup, recovered.Source);
        AssertDocumentEqual(second, AssertEx.NotNull(recovered.Document));
    }

    public static void ReportsRecoveryWhenPrimaryIsCorrupt()
    {
        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);
        ContinuationDocument first = Document('A');
        ContinuationDocument second = Document('B');

        AssertEx.True(store.SaveAtomic(RelativePath, first).Success);
        ContinuationSaveSnapshot secondSave = AssertEx.NotNull(
            store.SaveAtomic(RelativePath, second).Value);
        File.WriteAllText(secondSave.FullPath, "not-json");

        ContinuationLoadSnapshot recovered = Load(store, RelativePath);
        AssertEx.True(recovered.RecoveredFromBackup);
        AssertEx.Equal(ContinuationLoadSource.Backup, recovered.Source);
        AssertDocumentEqual(first, AssertEx.NotNull(recovered.Document));
        AssertEx.Contains(
            recovered.Notices,
            notice => notice.Code == "PrimaryUnreadable",
            "Expected an explicit primary-unreadable recovery notice.");
    }

    public static void RejectsTraversalAbsolutePathsAndOfficialExtensions()
    {
        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);
        ContinuationDocument document = Document('A');

        AssertEx.False(store.SaveAtomic("../escape.aavt.json", document).Success);
        AssertEx.False(store.SaveAtomic("project.aap", document).Success);
        AssertEx.False(store.SaveAtomic("save.aas", document).Success);
        AssertEx.False(
            store.SaveAtomic(Path.Combine(files.Root, "absolute.aavt.json"), document).Success);

        string escapedPath = Path.GetFullPath(
            Path.Combine(store.RootDirectory, "..", "escape.aavt.json"));
        AssertEx.False(File.Exists(escapedPath));
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void FailedReplacementLeavesPrimaryUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);
        ContinuationDocument first = Document('A');
        ContinuationDocument second = Document('B');
        ContinuationSaveSnapshot firstSave = AssertEx.NotNull(
            store.SaveAtomic(RelativePath, first).Value);

        using (new FileStream(
                   firstSave.FullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            Result<ContinuationSaveSnapshot> blockedSave = store.SaveAtomic(
                RelativePath,
                second);
            AssertEx.False(blockedSave.Success);
        }

        ContinuationLoadSnapshot loaded = Load(store, RelativePath);
        AssertEx.Equal(ContinuationLoadSource.Primary, loaded.Source);
        AssertDocumentEqual(first, AssertEx.NotNull(loaded.Document));

        string directory = AssertEx.NotNull(Path.GetDirectoryName(firstSave.FullPath));
        AssertEx.Equal(
            0,
            Directory.GetFiles(directory, "*.tmp.*", SearchOption.TopDirectoryOnly).Length);
    }

    public static void RejectsUnknownJsonPropertiesAndKeepsOfficialFilesUntouched()
    {
        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);
        ContinuationSaveSnapshot saved = AssertEx.NotNull(
            store.SaveAtomic(RelativePath, Document('A')).Value);
        string officialProject = files.Write("official.aap", "official-project-bytes");
        byte[] before = File.ReadAllBytes(officialProject);

        string json = File.ReadAllText(saved.FullPath);
        File.WriteAllText(
            saved.FullPath,
            json.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"unexpected\": true,",
                StringComparison.Ordinal));

        Result<ContinuationLoadSnapshot> load = store.TryLoad(RelativePath);
        AssertEx.False(load.Success);
        AssertEx.True(
            load.Error.Contains("Unknown JSON property", StringComparison.Ordinal));
        AssertEx.True(before.SequenceEqual(File.ReadAllBytes(officialProject)));
    }

    public static void RejectsInvalidDocumentBeforeCreatingFiles()
    {
        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);
        ContinuationDocument invalid = Document('A') with
        {
            ProjectRevisionSha256 = "not-a-hash"
        };

        Result<ContinuationSaveSnapshot> save = store.SaveAtomic(RelativePath, invalid);
        AssertEx.False(save.Success);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void MissingSidecarReturnsNoneWithoutCreatingRoot()
    {
        using var files = new TemporaryAapDirectory();
        var store = CreateStore(files);

        ContinuationLoadSnapshot load = Load(store, "missing.aavt.json");
        AssertEx.Equal(ContinuationLoadSource.None, load.Source);
        AssertEx.True(load.Document == null);
        AssertEx.Equal(0, load.Notices.Count);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void ContendingWriterCannotReplacePrimary()
    {
        using var files = new TemporaryAapDirectory();
        var firstStore = CreateStore(files);
        ContinuationDocument first = Document('A');
        ContinuationDocument second = Document('B');
        ContinuationSaveSnapshot initial = AssertEx.NotNull(
            firstStore.SaveAtomic(RelativePath, first).Value);

        using (new FileStream(
                   initial.FullPath + ".lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var contendingStore = new JsonContinuationStore(
                firstStore.RootDirectory,
                new JsonContinuationStoreOptions(
                    LockRetryAttempts: 2,
                    LockRetryDelayMilliseconds: 0));
            Result<ContinuationSaveSnapshot> save = contendingStore.SaveAtomic(
                RelativePath,
                second);
            AssertEx.False(save.Success);
            AssertEx.True(save.Error.Contains("lock", StringComparison.OrdinalIgnoreCase));
        }

        ContinuationLoadSnapshot loaded = Load(firstStore, RelativePath);
        AssertDocumentEqual(first, AssertEx.NotNull(loaded.Document));
    }

    private static JsonContinuationStore CreateStore(TemporaryAapDirectory files) =>
        new(Path.Combine(files.Root, "sidecars"));

    private static ContinuationLoadSnapshot Load(
        JsonContinuationStore store,
        string relativePath)
    {
        Result<ContinuationLoadSnapshot> result = store.TryLoad(relativePath);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static ContinuationDocument Document(char revisionCharacter) => new(
        ContinuationDocument.CurrentSchemaVersion,
        ProjectId,
        new string('1', 64),
        new string(revisionCharacter, 64),
        new[]
        {
            new ContinuationRule(new SceneKey(NodeGuid, 1, new string('D', 64)))
        });

    private static void AssertDocumentEqual(
        ContinuationDocument expected,
        ContinuationDocument actual)
    {
        AssertEx.Equal(expected.SchemaVersion, actual.SchemaVersion);
        AssertEx.Equal(expected.ProjectId, actual.ProjectId);
        AssertEx.Equal(expected.ProjectPathKey, actual.ProjectPathKey);
        AssertEx.Equal(expected.ProjectRevisionSha256, actual.ProjectRevisionSha256);
        AssertEx.Equal(expected.Rules.Count, actual.Rules.Count);
        for (int index = 0; index < expected.Rules.Count; index++)
        {
            AssertEx.Equal(expected.Rules[index], actual.Rules[index]);
        }
    }
}
