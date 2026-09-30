using System.Text;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Workspace;

namespace AzureArchive.VideoTools.Tests;

internal static class JsonCommandWorkspaceStoreTests
{
    public static void RoundTripsTimelineAndProjectionWithAtomicBackups()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument first = fixture.Timeline("#char;3;move;dx=100");
        CommandTimelineDocument second = first with
        {
            Entries = Array.AsReadOnly(new[]
            {
                first.Entries[0] with
                {
                    Commands = Array.AsReadOnly(new[]
                    {
                        first.Entries[0].Commands[0] with
                        {
                            Directive = "#char;3;move;dx=200;duration=300"
                        }
                    })
                }
            })
        };

        CommandWorkspaceSaveSnapshot firstTimeline = AssertEx.NotNull(
            fixture.SaveTimeline(first).Value);
        AssertEx.False(firstTimeline.ReplacedExisting);
        AssertEx.False(firstTimeline.BackupCreated);
        CommandWorkspaceSaveSnapshot firstProjection = AssertEx.NotNull(
            fixture.SaveProjection(first).Value);

        CommandWorkspaceSaveSnapshot secondTimeline = AssertEx.NotNull(
            fixture.SaveTimeline(second).Value);
        CommandWorkspaceSaveSnapshot secondProjection = AssertEx.NotNull(
            fixture.SaveProjection(second).Value);
        AssertEx.True(secondTimeline.ReplacedExisting);
        AssertEx.True(secondTimeline.BackupCreated);
        AssertEx.True(secondProjection.ReplacedExisting);
        AssertEx.True(secondProjection.BackupCreated);
        AssertEx.True(File.Exists(
            secondTimeline.FullPath + JsonCommandWorkspaceStore.BackupSuffix));
        AssertEx.True(File.Exists(
            secondProjection.FullPath + JsonCommandWorkspaceStore.BackupSuffix));

        CommandTimelineLoadSnapshot timelineLoad = AssertEx.NotNull(
            fixture.Store.TryLoadTimeline(
                fixture.Project.Source.PathKey,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.Equal(CommandWorkspaceDocumentStatus.Ready, timelineLoad.Status);
        AssertEx.Equal(CommandWorkspaceLoadSource.Primary, timelineLoad.Source);
        AssertEx.Equal(
            "#char;3;move;dx=200;duration=300",
            AssertEx.NotNull(timelineLoad.Timeline).Entries[0].Commands[0].Directive);

        PlaybackCommandProjectionLoadSnapshot projectionLoad = AssertEx.NotNull(
            fixture.Store.TryLoadProjection(
                fixture.Project.Source.PathKey,
                second,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.Equal(CommandWorkspaceDocumentStatus.Ready, projectionLoad.Status);
        AssertEx.Equal(CommandWorkspaceLoadSource.Primary, projectionLoad.Source);
        AssertEx.Equal(
            "#char;3;move;dx=200;duration=300;easing=linear",
            AssertEx.NotNull(projectionLoad.Projection)
                .Batches[0].Commands[0].CanonicalDirective);

        string json = File.ReadAllText(secondTimeline.FullPath);
        AssertEx.True(
            json.Contains("\"phase\": \"sceneEnter\"", StringComparison.Ordinal),
            "Expected string enum serialization for command phase.");
        AssertEx.True(
            firstProjection.FullPath.EndsWith(
                JsonCommandWorkspaceStore.ProjectionFileName,
                StringComparison.Ordinal));
    }

    public static void CorruptTimelinePrimaryRecoversValidatedBackup()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument first = fixture.Timeline("#char;3;move;dx=100");
        CommandTimelineDocument second = fixture.Timeline("#char;3;move;dx=200");
        AssertEx.True(fixture.SaveTimeline(first).Success);
        CommandWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            fixture.SaveTimeline(second).Value);
        File.WriteAllText(saved.FullPath, "{ broken-json }");

        CommandTimelineLoadSnapshot loaded = AssertEx.NotNull(
            fixture.Store.TryLoadTimeline(
                fixture.Project.Source.PathKey,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);

        AssertEx.True(loaded.RecoveredFromBackup);
        AssertEx.Equal(CommandWorkspaceLoadSource.Backup, loaded.Source);
        AssertEx.Equal(
            "#char;3;move;dx=100",
            AssertEx.NotNull(loaded.Timeline).Entries[0].Commands[0].Directive);
    }

    public static void FutureTimelineIsAuthoritativeAndCannotBeOverwritten()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument first = fixture.Timeline("#char;3;move;dx=100");
        CommandTimelineDocument second = fixture.Timeline("#char;3;move;dx=200");
        AssertEx.True(fixture.SaveTimeline(first).Success);
        CommandWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            fixture.SaveTimeline(second).Value);
        byte[] future = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":99,\"futureProperty\":true}");
        File.WriteAllBytes(saved.FullPath, future);

        CommandTimelineLoadSnapshot loaded = AssertEx.NotNull(
            fixture.Store.TryLoadTimeline(
                fixture.Project.Source.PathKey,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.Equal(CommandWorkspaceLoadSource.Primary, loaded.Source);
        AssertEx.Equal(CommandWorkspaceDocumentStatus.FutureSchema, loaded.Status);
        AssertEx.Equal(99, loaded.DetectedSchemaVersion);
        AssertEx.True(loaded.Timeline == null);
        AssertEx.False(loaded.RecoveredFromBackup);

        Result<CommandWorkspaceSaveSnapshot> replacement =
            fixture.SaveTimeline(second);
        AssertEx.False(replacement.Success);
        AssertEx.True(future.SequenceEqual(File.ReadAllBytes(saved.FullPath)));
    }

    public static void RejectsUnknownDuplicateAndNumericEnumJson()
    {
        using var unknownFixture = new CommandStoreFixture();
        CommandWorkspaceSaveSnapshot unknownPath = AssertEx.NotNull(
            unknownFixture.SaveTimeline(
                unknownFixture.Timeline("#char;3;move;dx=100")).Value);
        string valid = File.ReadAllText(unknownPath.FullPath);
        File.WriteAllText(
            unknownPath.FullPath,
            valid.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"unexpected\": true,",
                StringComparison.Ordinal));
        AssertEx.False(unknownFixture.Store.TryLoadTimeline(
            unknownFixture.Project.Source.PathKey,
            unknownFixture.Workspace,
            unknownFixture.Project,
            unknownFixture.Playback).Success);

        using var duplicateFixture = new CommandStoreFixture();
        CommandWorkspaceSaveSnapshot duplicatePath = AssertEx.NotNull(
            duplicateFixture.SaveTimeline(
                duplicateFixture.Timeline("#char;3;move;dx=100")).Value);
        valid = File.ReadAllText(duplicatePath.FullPath);
        File.WriteAllText(
            duplicatePath.FullPath,
            valid.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
                StringComparison.Ordinal));
        AssertEx.False(duplicateFixture.Store.TryLoadTimeline(
            duplicateFixture.Project.Source.PathKey,
            duplicateFixture.Workspace,
            duplicateFixture.Project,
            duplicateFixture.Playback).Success);

        using var enumFixture = new CommandStoreFixture();
        CommandWorkspaceSaveSnapshot enumPath = AssertEx.NotNull(
            enumFixture.SaveTimeline(
                enumFixture.Timeline("#char;3;move;dx=100")).Value);
        valid = File.ReadAllText(enumPath.FullPath);
        File.WriteAllText(
            enumPath.FullPath,
            valid.Replace("\"phase\": \"sceneEnter\"", "\"phase\": 0", StringComparison.Ordinal));
        AssertEx.False(enumFixture.Store.TryLoadTimeline(
            enumFixture.Project.Source.PathKey,
            enumFixture.Workspace,
            enumFixture.Project,
            enumFixture.Playback).Success);
    }

    public static void CorruptProjectionPrimaryRecoversReproducibleBackup()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument timeline = fixture.Timeline(
            "#char;3;move;dx=100");
        AssertEx.True(fixture.SaveProjection(timeline).Success);
        CommandWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            fixture.SaveProjection(timeline).Value);
        File.WriteAllText(saved.FullPath, "{ broken-json }");

        PlaybackCommandProjectionLoadSnapshot loaded = AssertEx.NotNull(
            fixture.Store.TryLoadProjection(
                fixture.Project.Source.PathKey,
                timeline,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);

        AssertEx.True(loaded.RecoveredFromBackup);
        AssertEx.Equal(CommandWorkspaceLoadSource.Backup, loaded.Source);
        AssertEx.Equal(CommandWorkspaceDocumentStatus.Ready, loaded.Status);
        AssertEx.Equal(1, AssertEx.NotNull(loaded.Projection).Batches.Count);
    }

    public static void RejectsHandcraftedProjectionThatCannotBeRecompiled()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument timeline = fixture.Timeline(
            "#char;3;move;dx=100");
        CommandWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            fixture.SaveProjection(timeline).Value);
        string json = File.ReadAllText(saved.FullPath);
        File.WriteAllText(
            saved.FullPath,
            json.Replace(
                "#char;3;move;dx=100;duration=0;easing=linear",
                "#char;3;move;dx=101;duration=0;easing=linear",
                StringComparison.Ordinal));

        Result<PlaybackCommandProjectionLoadSnapshot> loaded =
            fixture.Store.TryLoadProjection(
                fixture.Project.Source.PathKey,
                timeline,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback);

        AssertEx.False(loaded.Success);
        AssertEx.True(
            loaded.Error.Contains("trusted compiler", StringComparison.Ordinal),
            loaded.Error);
    }

    public static void FutureProjectionIsAuthoritativeAndCannotBeOverwritten()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument timeline = fixture.Timeline(
            "#char;3;move;dx=100");
        AssertEx.True(fixture.SaveProjection(timeline).Success);
        CommandWorkspaceSaveSnapshot saved = AssertEx.NotNull(
            fixture.SaveProjection(timeline).Value);
        byte[] future = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":99,\"futureProperty\":true}");
        File.WriteAllBytes(saved.FullPath, future);

        PlaybackCommandProjectionLoadSnapshot loaded = AssertEx.NotNull(
            fixture.Store.TryLoadProjection(
                fixture.Project.Source.PathKey,
                timeline,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.Equal(CommandWorkspaceDocumentStatus.FutureSchema, loaded.Status);
        AssertEx.Equal(CommandWorkspaceLoadSource.Primary, loaded.Source);
        AssertEx.True(loaded.Projection == null);

        Result<CommandWorkspaceSaveSnapshot> replacement =
            fixture.SaveProjection(timeline);
        AssertEx.False(replacement.Success);
        AssertEx.True(future.SequenceEqual(File.ReadAllBytes(saved.FullPath)));
    }

    public static void MissingAndInvalidKeysNeverCreateWorkspaceRoot()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineLoadSnapshot missing = AssertEx.NotNull(
            fixture.Store.TryLoadTimeline(
                fixture.Project.Source.PathKey,
                fixture.Workspace,
                fixture.Project,
                fixture.Playback).Value);
        AssertEx.Equal(CommandWorkspaceDocumentStatus.NotFound, missing.Status);
        AssertEx.False(Directory.Exists(fixture.Store.RootDirectory));

        Result<CommandWorkspaceSaveSnapshot> invalid =
            fixture.Store.SaveTimelineAtomic(
                "../project.aap",
                fixture.Timeline("#char;3;move;dx=100"),
                fixture.Workspace,
                fixture.Project,
                fixture.Playback);
        AssertEx.False(invalid.Success);
        AssertEx.False(Directory.Exists(fixture.Store.RootDirectory));
    }

    public static void ContendingWriterCannotReplaceTimeline()
    {
        using var fixture = new CommandStoreFixture();
        CommandTimelineDocument first = fixture.Timeline("#char;3;move;dx=100");
        CommandTimelineDocument second = fixture.Timeline("#char;3;move;dx=200");
        CommandWorkspaceSaveSnapshot initial = AssertEx.NotNull(
            fixture.SaveTimeline(first).Value);
        byte[] original = File.ReadAllBytes(initial.FullPath);

        using (new FileStream(
                   initial.FullPath + ".lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var contender = new JsonCommandWorkspaceStore(
                fixture.Store.RootDirectory,
                new JsonCommandWorkspaceStoreOptions(
                    LockRetryAttempts: 1,
                    LockRetryDelayMilliseconds: 0));
            Result<CommandWorkspaceSaveSnapshot> result =
                contender.SaveTimelineAtomic(
                    fixture.Project.Source.PathKey,
                    second,
                    fixture.Workspace,
                    fixture.Project,
                    fixture.Playback);
            AssertEx.False(result.Success);
        }

        AssertEx.True(original.SequenceEqual(File.ReadAllBytes(initial.FullPath)));
    }

    private sealed class CommandStoreFixture : IDisposable
    {
        private readonly TemporaryAapDirectory _files = new();

        public CommandStoreFixture()
        {
            string projectPath = _files.Write(
                "command-store.aap",
                SyntheticAap.Project(new[]
                {
                    SyntheticAap.Scene("A"),
                    SyntheticAap.Scene("B")
                }));
            Result<ProjectSnapshot> project = new AapProjectReader().Read(projectPath);
            AssertEx.True(project.Success, project.Error);
            Project = AssertEx.NotNull(project.Value);
            Playback = CreatePlayback(Project);
            Workspace = new ModWorkspaceManifest(
                ModWorkspaceManifest.CurrentSchemaVersion,
                Guid.NewGuid().ToString("D"),
                "0.7.17",
                "0.7.17",
                new ModWorkspaceSourceBinding(
                    Project.Source.PathKey,
                    Project.Source.RevisionSha256,
                    Playback.Source.PathKey,
                    Playback.Source.RevisionSha256,
                    Playback.SchemaName),
                Array.AsReadOnly(new[]
                {
                    CharacterTransformCommandFamilyCompiler.CapabilityId
                }));
            Store = new JsonCommandWorkspaceStore(
                Path.Combine(_files.Root, "workspaces"));
        }

        public ProjectSnapshot Project { get; }

        public PlaybackArchiveSnapshot Playback { get; }

        public ModWorkspaceManifest Workspace { get; }

        public JsonCommandWorkspaceStore Store { get; }

        public CommandTimelineDocument Timeline(string directive) => new(
            CommandTimelineDocument.CurrentSchemaVersion,
            Workspace.WorkspaceId,
            Project.Source.PathKey,
            Project.Source.RevisionSha256,
            Array.AsReadOnly(new[]
            {
                new CommandTimelineEntry(
                    Project.ScriptNodes.Single().Scenes[0].Key,
                    Array.AsReadOnly(new[]
                    {
                        new AuthoringCommand(
                            Guid.NewGuid().ToString("D"),
                            0,
                            CommandTimelinePhase.SceneEnter,
                            CharacterTransformCommandFamilyCompiler.CommandTypeId,
                            directive,
                            Enabled: true)
                    }))
            }));

        public Result<CommandWorkspaceSaveSnapshot> SaveTimeline(
            CommandTimelineDocument timeline) => Store.SaveTimelineAtomic(
            Project.Source.PathKey,
            timeline,
            Workspace,
            Project,
            Playback);

        public Result<CommandWorkspaceSaveSnapshot> SaveProjection(
            CommandTimelineDocument timeline) =>
            Store.CompileAndSaveProjectionAtomic(
                Project.Source.PathKey,
                timeline,
                Workspace,
                Project,
                Playback);

        public void Dispose() => _files.Dispose();

        private static PlaybackArchiveSnapshot CreatePlayback(
            ProjectSnapshot project)
        {
            string fullPath = Path.ChangeExtension(project.Source.FullPath, ".aas");
            var source = new PlaybackArchiveSourceSnapshot(
                fullPath,
                new string('B', 64),
                new string('C', 64),
                2,
                project.Source.LastWriteTimeUtc + TimeSpan.FromSeconds(1));
            return new PlaybackArchiveSnapshot(
                source,
                "synthetic/v1",
                Array.AsReadOnly(new[]
                {
                    Record(0, "A", "compiled A"),
                    Record(1, "B", "compiled B")
                }));
        }

        private static PlaybackRecordSnapshot Record(
            int index,
            string text,
            string compiledScript) => new(
            index,
            0,
            0,
            0,
            string.Empty,
            0,
            0,
            0,
            string.Empty,
            compiledScript,
            text,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            index.ToString("X64"));
    }
}
