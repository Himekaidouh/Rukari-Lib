using System.Text;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Workspace;

namespace AzureArchive.VideoTools.Tests;

internal static class JsonModWorkspaceStoreTests
{
    public static void RoundTripsAndRotatesAtomicBackup()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);
        ModWorkspaceManifest first = Manifest();
        ModWorkspaceManifest second = first with
        {
            CreatedWithPluginVersion = "0.7.18"
        };

        Result<ModWorkspaceSaveSnapshot> firstSave = store.SaveAtomic(
            ProjectKey,
            first,
            Context());
        AssertEx.True(firstSave.Success, firstSave.Error);
        ModWorkspaceSaveSnapshot firstSnapshot = AssertEx.NotNull(firstSave.Value);
        AssertEx.False(firstSnapshot.ReplacedExisting);
        AssertEx.False(firstSnapshot.BackupCreated);

        Result<ModWorkspaceSaveSnapshot> secondSave = store.SaveAtomic(
            ProjectKey,
            second,
            Context());
        AssertEx.True(secondSave.Success, secondSave.Error);
        ModWorkspaceSaveSnapshot secondSnapshot = AssertEx.NotNull(secondSave.Value);
        AssertEx.True(secondSnapshot.ReplacedExisting);
        AssertEx.True(secondSnapshot.BackupCreated);
        AssertEx.True(File.Exists(
            secondSnapshot.ManifestPath + JsonModWorkspaceStore.BackupSuffix));

        ModWorkspaceLoadSnapshot loaded = Load(store, Context());
        AssertEx.Equal(ModWorkspaceLoadSource.Primary, loaded.Source);
        AssertEx.Equal(ModWorkspaceCompatibilityStatus.Ready, loaded.Compatibility.Status);
        AssertManifestEqual(second, AssertEx.NotNull(loaded.Manifest));
    }

    public static void CorruptPrimaryRecoversValidatedBackup()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);
        ModWorkspaceManifest first = Manifest();
        ModWorkspaceManifest second = first with
        {
            CreatedWithPluginVersion = "0.7.18"
        };
        AssertEx.True(store.SaveAtomic(ProjectKey, first, Context()).Success);
        ModWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            store.SaveAtomic(ProjectKey, second, Context()).Value);
        File.WriteAllText(saved.ManifestPath, "{ broken-json }");

        ModWorkspaceLoadSnapshot loaded = Load(store, Context());
        AssertEx.True(loaded.RecoveredFromBackup);
        AssertEx.Equal(ModWorkspaceLoadSource.Backup, loaded.Source);
        AssertManifestEqual(first, AssertEx.NotNull(loaded.Manifest));
        AssertEx.Contains(
            loaded.Notices,
            notice => notice.Code == "PrimaryUnreadable",
            "Expected an explicit backup recovery notice.");
    }

    public static void FutureSchemaIsAuthoritativeAndNeverFallsBack()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);
        ModWorkspaceManifest first = Manifest();
        ModWorkspaceManifest second = first with
        {
            CreatedWithPluginVersion = "0.7.18"
        };
        AssertEx.True(store.SaveAtomic(ProjectKey, first, Context()).Success);
        ModWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            store.SaveAtomic(ProjectKey, second, Context()).Value);

        string futureJson = """
            {
              "schemaVersion": 99,
              "futureProperty": { "unknown": true }
            }
            """;
        File.WriteAllText(
            saved.ManifestPath,
            futureJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ModWorkspaceLoadSnapshot loaded = Load(store, Context());
        AssertEx.Equal(ModWorkspaceLoadSource.Primary, loaded.Source);
        AssertEx.Equal(99, loaded.DetectedSchemaVersion);
        AssertEx.True(loaded.Manifest == null);
        AssertEx.Equal(
            ModWorkspaceCompatibilityStatus.FutureSchema,
            loaded.Compatibility.Status);
        AssertEx.False(loaded.Compatibility.CanWrite);
        AssertEx.False(loaded.Compatibility.CanExecute);
        AssertEx.False(loaded.RecoveredFromBackup);
    }

    public static void RefusesToOverwriteFutureSchema()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);
        ModWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            store.SaveAtomic(ProjectKey, Manifest(), Context()).Value);
        byte[] future = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":99,\"futureProperty\":true}");
        File.WriteAllBytes(saved.ManifestPath, future);

        Result<ModWorkspaceSaveSnapshot> replacement = store.SaveAtomic(
            ProjectKey,
            Manifest(),
            Context());
        AssertEx.False(replacement.Success);
        AssertEx.True(future.SequenceEqual(File.ReadAllBytes(saved.ManifestPath)));
    }

    public static void SourceMismatchLoadsReadOnlyAndDoesNotUseBackup()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);
        ModWorkspaceManifest first = Manifest();
        ModWorkspaceManifest second = first with
        {
            CreatedWithPluginVersion = "0.7.18"
        };
        AssertEx.True(store.SaveAtomic(ProjectKey, first, Context()).Success);
        AssertEx.True(store.SaveAtomic(ProjectKey, second, Context()).Success);

        ModWorkspaceRuntimeContext changed = Context() with
        {
            CurrentSource = Context().CurrentSource with
            {
                PlaybackRevisionSha256 = new string('F', 64)
            }
        };
        ModWorkspaceLoadSnapshot loaded = Load(store, changed);
        AssertEx.Equal(ModWorkspaceLoadSource.Primary, loaded.Source);
        AssertEx.Equal(
            ModWorkspaceCompatibilityStatus.SourceMismatch,
            loaded.Compatibility.Status);
        AssertEx.False(loaded.Compatibility.CanExecute);
        AssertEx.False(loaded.RecoveredFromBackup);
    }

    public static void RejectsInvalidKeysAndManifestFolderMismatch()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);

        AssertEx.False(store.SaveAtomic(
            "../project.aap",
            Manifest(),
            Context()).Success);
        AssertEx.False(store.SaveAtomic(
            new string('E', 64),
            Manifest(),
            Context()).Success);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void RejectsUnknownAndDuplicateCurrentProperties()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);
        ModWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            store.SaveAtomic(ProjectKey, Manifest(), Context()).Value);

        string json = File.ReadAllText(saved.ManifestPath);
        File.WriteAllText(
            saved.ManifestPath,
            json.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"unexpected\": true,",
                StringComparison.Ordinal));
        AssertEx.False(store.TryLoad(ProjectKey, Context()).Success);

        File.WriteAllText(
            saved.ManifestPath,
            json.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
                StringComparison.Ordinal));
        AssertEx.False(store.TryLoad(ProjectKey, Context()).Success);
    }

    public static void MissingWorkspaceDoesNotCreateRoot()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore store = CreateStore(files);

        ModWorkspaceLoadSnapshot loaded = Load(store, Context());
        AssertEx.Equal(ModWorkspaceLoadSource.None, loaded.Source);
        AssertEx.Equal(
            ModWorkspaceCompatibilityStatus.NotFound,
            loaded.Compatibility.Status);
        AssertEx.False(Directory.Exists(store.RootDirectory));
    }

    public static void ContendingWriterCannotReplaceManifest()
    {
        using var files = new TemporaryAapDirectory();
        JsonModWorkspaceStore firstStore = CreateStore(files);
        ModWorkspaceManifest first = Manifest();
        ModWorkspaceManifest second = first with
        {
            CreatedWithPluginVersion = "0.7.18"
        };
        ModWorkspaceSaveSnapshot initial = AssertEx.NotNull(
            firstStore.SaveAtomic(ProjectKey, first, Context()).Value);

        using (new FileStream(
                   initial.ManifestPath + ".lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var contendingStore = new JsonModWorkspaceStore(
                firstStore.RootDirectory,
                new JsonModWorkspaceStoreOptions(
                    LockRetryAttempts: 2,
                    LockRetryDelayMilliseconds: 0));
            Result<ModWorkspaceSaveSnapshot> save = contendingStore.SaveAtomic(
                ProjectKey,
                second,
                Context());
            AssertEx.False(save.Success);
            AssertEx.True(save.Error.Contains(
                "lock",
                StringComparison.OrdinalIgnoreCase));
        }

        ModWorkspaceLoadSnapshot loaded = Load(firstStore, Context());
        AssertManifestEqual(first, AssertEx.NotNull(loaded.Manifest));
    }

    private static string ProjectKey =>
        ModWorkspaceCompatibilityTests.Source().ProjectPathKey;

    private static ModWorkspaceManifest Manifest() =>
        ModWorkspaceCompatibilityTests.Manifest();

    private static ModWorkspaceRuntimeContext Context() =>
        ModWorkspaceCompatibilityTests.Context();

    private static JsonModWorkspaceStore CreateStore(
        TemporaryAapDirectory files) => new(
        Path.Combine(files.Root, "BepInEx", "config", "AzureArchive.VideoTools", "workspaces"));

    private static ModWorkspaceLoadSnapshot Load(
        JsonModWorkspaceStore store,
        ModWorkspaceRuntimeContext context)
    {
        Result<ModWorkspaceLoadSnapshot> result = store.TryLoad(
            ProjectKey,
            context);
        AssertEx.True(result.Success, result.Error);
        return AssertEx.NotNull(result.Value);
    }

    private static void AssertManifestEqual(
        ModWorkspaceManifest expected,
        ModWorkspaceManifest actual)
    {
        AssertEx.Equal(expected.SchemaVersion, actual.SchemaVersion);
        AssertEx.Equal(expected.WorkspaceId, actual.WorkspaceId);
        AssertEx.Equal(
            expected.CreatedWithPluginVersion,
            actual.CreatedWithPluginVersion);
        AssertEx.Equal(
            expected.MinimumPluginVersion,
            actual.MinimumPluginVersion);
        AssertEx.Equal(expected.Source, actual.Source);
        AssertEx.Equal(
            expected.RequiredCapabilities.Count,
            actual.RequiredCapabilities.Count);
        for (int index = 0; index < expected.RequiredCapabilities.Count; index++)
        {
            AssertEx.Equal(
                expected.RequiredCapabilities[index],
                actual.RequiredCapabilities[index]);
        }
    }
}
