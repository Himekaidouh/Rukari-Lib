using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;

namespace AzureArchive.VideoTools.Formats.Workspace;

public sealed record CommandAuthoringEntrySnapshot(
    int? PlaybackRecordIndex,
    SceneKey Scene,
    string CommandId,
    int Order,
    int PublicSlot,
    string Directive,
    bool Enabled);

public sealed record CommandAuthoringListSnapshot(
    string ProjectPathKey,
    string ProjectRevisionSha256,
    string PlaybackRevisionSha256,
    string WorkspaceId,
    IReadOnlyList<CommandAuthoringEntrySnapshot> Commands);

public sealed record CommandAuthoringSceneSnapshot(
    int PlaybackRecordIndex,
    SceneKey Scene,
    string DialogueText,
    ScenePlaybackMappingStatus MappingStatus);

public sealed record CommandAuthoringSceneListSnapshot(
    string ProjectRevisionSha256,
    string PlaybackRevisionSha256,
    IReadOnlyList<CommandAuthoringSceneSnapshot> Scenes);

public sealed record CommandAuthoringWriteSnapshot(
    string ProjectPathKey,
    string WorkspaceId,
    int PlaybackRecordIndex,
    SceneKey Scene,
    int PublicSlot,
    string CommandId,
    bool ReplacedExisting,
    int CompiledBatchCount,
    string TimelinePath,
    string ProjectionPath,
    bool OfficialFilesWritten);

public sealed class CommandWorkspaceAuthoringService
{
    private static readonly IReadOnlySet<string> AvailableCapabilities =
        new HashSet<string>(
            new[]
            {
                CharacterTransformCommandFamilyCompiler.CapabilityId,
                SlotPendingCommandFamilyCompiler.CapabilityId
            },
            StringComparer.Ordinal);

    private readonly AapProjectReader _projectReader = new();
    private readonly AasScenarioReader _playbackReader = new();
    private readonly ConservativeProjectPlaybackMapper _mapper = new();
    private readonly CommandTimelineCompiler _compiler;
    private readonly CommandTimelineEditor _editor = new();
    private readonly CharacterTransformCommandFamilyCompiler _characterCompiler = new();

    public CommandWorkspaceAuthoringService()
    {
        _compiler = new CommandTimelineCompiler(playbackMapper: _mapper);
    }

    public Result<CommandAuthoringWriteSnapshot> SetCharacterTransform(
        string projectPath,
        string playbackPath,
        string workspaceRoot,
        string toolVersion,
        int playbackRecordIndex,
        string directive)
    {
        Result<AuthoringSession> opened = Open(
            projectPath,
            playbackPath,
            workspaceRoot,
            toolVersion,
            allowCreate: true);
        if (!opened.Success || opened.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(opened.Error);
        }

        AuthoringSession session = opened.Value;
        Result<SceneKey> scene = ResolveRecordScene(
            session.Mapping,
            session.Playback,
            playbackRecordIndex);
        if (!scene.Success || scene.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(scene.Error);
        }

        Result<CommandTimelineEditSnapshot> edited =
            _editor.UpsertCharacterTransform(
                session.Timeline,
                scene.Value,
                directive);
        if (!edited.Success || edited.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(edited.Error);
        }

        return Commit(session, playbackRecordIndex, edited.Value);
    }

    public Result<CommandAuthoringWriteSnapshot> RemoveCharacterTransform(
        string projectPath,
        string playbackPath,
        string workspaceRoot,
        string toolVersion,
        int playbackRecordIndex,
        int publicSlot)
    {
        Result<AuthoringSession> opened = Open(
            projectPath,
            playbackPath,
            workspaceRoot,
            toolVersion,
            allowCreate: false);
        if (!opened.Success || opened.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(opened.Error);
        }

        AuthoringSession session = opened.Value;
        Result<SceneKey> scene = ResolveRecordScene(
            session.Mapping,
            session.Playback,
            playbackRecordIndex);
        if (!scene.Success || scene.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(scene.Error);
        }

        Result<CommandTimelineEditSnapshot> edited =
            _editor.RemoveCharacterTransform(
                session.Timeline,
                scene.Value,
                publicSlot);
        if (!edited.Success || edited.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(edited.Error);
        }

        return Commit(session, playbackRecordIndex, edited.Value);
    }

    public Result<CommandAuthoringListSnapshot> List(
        string projectPath,
        string playbackPath,
        string workspaceRoot,
        string toolVersion)
    {
        Result<AuthoringSession> opened = Open(
            projectPath,
            playbackPath,
            workspaceRoot,
            toolVersion,
            allowCreate: false);
        if (!opened.Success || opened.Value == null)
        {
            return Result<CommandAuthoringListSnapshot>.Fail(opened.Error);
        }

        AuthoringSession session = opened.Value;
        var recordsByScene = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ScenePlaybackMapping mapped in session.Mapping.Scenes)
        {
            if (mapped.PlaybackRecordIndex.HasValue)
            {
                recordsByScene[Location(mapped.Scene)] = mapped.PlaybackRecordIndex.Value;
            }
        }

        var commands = new List<CommandAuthoringEntrySnapshot>();
        foreach (CommandTimelineEntry entry in session.Timeline.Entries)
        {
            recordsByScene.TryGetValue(Location(entry.Scene), out int recordIndex);
            bool hasRecord = recordsByScene.ContainsKey(Location(entry.Scene));
            foreach (AuthoringCommand command in entry.Commands)
            {
                if (!string.Equals(
                        command.CommandType,
                        CharacterTransformCommandFamilyCompiler.CommandTypeId,
                        StringComparison.Ordinal))
                {
                    return Result<CommandAuthoringListSnapshot>.Fail(
                        $"Unsupported command family in authoring timeline: {command.CommandType}");
                }

                Result<CanonicalTimelineCommand> canonical =
                    _characterCompiler.Canonicalize(command.Directive);
                if (!canonical.Success || canonical.Value == null)
                {
                    return Result<CommandAuthoringListSnapshot>.Fail(
                        $"Command {command.CommandId} is invalid: {canonical.Error}");
                }

                commands.Add(new CommandAuthoringEntrySnapshot(
                    hasRecord ? recordIndex : null,
                    entry.Scene,
                    command.CommandId,
                    command.Order,
                    canonical.Value.PublicSlot,
                    canonical.Value.Directive,
                    command.Enabled));
            }
        }

        return Result<CommandAuthoringListSnapshot>.Ok(new(
            session.Project.Source.PathKey,
            session.Project.Source.RevisionSha256,
            session.Playback.Source.RevisionSha256,
            session.Workspace.WorkspaceId,
            Array.AsReadOnly(commands.ToArray())));
    }

    public Result<CommandAuthoringSceneListSnapshot> ListScenes(
        string projectPath,
        string playbackPath)
    {
        Result<string> normalizedProject = NormalizeSource(projectPath, ".aap", "project");
        Result<string> normalizedPlayback = NormalizeSource(playbackPath, ".aas", "playback");
        if (!normalizedProject.Success || normalizedProject.Value == null
            || !normalizedPlayback.Success || normalizedPlayback.Value == null)
        {
            return Result<CommandAuthoringSceneListSnapshot>.Fail(
                normalizedProject.Error.Length != 0
                    ? normalizedProject.Error
                    : normalizedPlayback.Error);
        }

        Result<ProjectSnapshot> projectRead = _projectReader.Read(normalizedProject.Value);
        Result<PlaybackArchiveSnapshot> playbackRead =
            _playbackReader.Read(normalizedPlayback.Value);
        if (!projectRead.Success || projectRead.Value == null
            || !playbackRead.Success || playbackRead.Value == null)
        {
            return Result<CommandAuthoringSceneListSnapshot>.Fail(
                $"Source read failed: AAP={projectRead.Error}; AAS={playbackRead.Error}");
        }

        Result<ProjectPlaybackMappingReport> mapping =
            _mapper.Map(projectRead.Value, playbackRead.Value);
        if (!mapping.Success || mapping.Value == null)
        {
            return Result<CommandAuthoringSceneListSnapshot>.Fail(
                $"Project/playback mapping failed: {mapping.Error}");
        }

        Dictionary<string, SceneSnapshot> projectScenes = projectRead.Value.ScriptNodes
            .SelectMany(node => node.Scenes)
            .ToDictionary(scene => Location(scene.Key), StringComparer.OrdinalIgnoreCase);
        CommandAuthoringSceneSnapshot[] scenes = mapping.Value.Scenes
            .Where(scene => scene.PlaybackRecordIndex.HasValue
                && scene.Status is (
                    ScenePlaybackMappingStatus.UniqueDialogueTextMatch
                    or ScenePlaybackMappingStatus.AnchoredNodeSequenceMatch))
            .Select(scene => new CommandAuthoringSceneSnapshot(
                scene.PlaybackRecordIndex!.Value,
                scene.Scene,
                projectScenes[Location(scene.Scene)].DialogueText,
                scene.Status))
            .OrderBy(scene => scene.PlaybackRecordIndex)
            .ToArray();

        return Result<CommandAuthoringSceneListSnapshot>.Ok(new(
            projectRead.Value.Source.RevisionSha256,
            playbackRead.Value.Source.RevisionSha256,
            Array.AsReadOnly(scenes)));
    }

    private Result<CommandAuthoringWriteSnapshot> Commit(
        AuthoringSession session,
        int playbackRecordIndex,
        CommandTimelineEditSnapshot edit)
    {
        Result<PlaybackCommandProjection> compiled = _compiler.Compile(
            session.Project,
            session.Playback,
            session.Workspace,
            edit.Timeline);
        if (!compiled.Success || compiled.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(
                $"Edited command timeline did not compile: {compiled.Error}");
        }

        if (session.WorkspaceIsNew)
        {
            Result<ModWorkspaceSaveSnapshot> manifestSave =
                session.WorkspaceStore.SaveAtomic(
                    session.Project.Source.PathKey,
                    session.Workspace,
                    session.Context);
            if (!manifestSave.Success)
            {
                return Result<CommandAuthoringWriteSnapshot>.Fail(
                    $"Workspace manifest save failed: {manifestSave.Error}");
            }
        }

        Result<CommandWorkspaceSaveSnapshot> timelineSave =
            session.CommandStore.SaveTimelineAtomic(
                session.Project.Source.PathKey,
                edit.Timeline,
                session.Workspace,
                session.Project,
                session.Playback);
        if (!timelineSave.Success || timelineSave.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(
                $"Command timeline save failed: {timelineSave.Error}");
        }

        Result<CommandWorkspaceSaveSnapshot> projectionSave =
            session.CommandStore.CompileAndSaveProjectionAtomic(
                session.Project.Source.PathKey,
                edit.Timeline,
                session.Workspace,
                session.Project,
                session.Playback);
        if (!projectionSave.Success || projectionSave.Value == null)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(
                "Command projection save failed after the timeline was committed; "
                + "runtime loading will fail closed until compilation succeeds: "
                + projectionSave.Error);
        }

        Result<CommandTimelineLoadSnapshot> timelineReload =
            session.CommandStore.TryLoadTimeline(
                session.Project.Source.PathKey,
                session.Workspace,
                session.Project,
                session.Playback);
        if (!timelineReload.Success
            || timelineReload.Value?.Timeline == null
            || timelineReload.Value.Status != CommandWorkspaceDocumentStatus.Ready)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(
                $"Committed command timeline did not reload: {timelineReload.Error}");
        }

        Result<PlaybackCommandProjectionLoadSnapshot> projectionReload =
            session.CommandStore.TryLoadProjection(
                session.Project.Source.PathKey,
                timelineReload.Value.Timeline,
                session.Workspace,
                session.Project,
                session.Playback);
        if (!projectionReload.Success
            || projectionReload.Value?.Projection == null
            || projectionReload.Value.Status != CommandWorkspaceDocumentStatus.Ready)
        {
            return Result<CommandAuthoringWriteSnapshot>.Fail(
                $"Committed command projection did not reload: {projectionReload.Error}");
        }

        return Result<CommandAuthoringWriteSnapshot>.Ok(new(
            session.Project.Source.PathKey,
            session.Workspace.WorkspaceId,
            playbackRecordIndex,
            edit.Scene,
            edit.PublicSlot,
            edit.CommandId,
            edit.ReplacedExisting,
            projectionReload.Value.Projection.Batches.Count,
            timelineSave.Value.FullPath,
            projectionSave.Value.FullPath,
            OfficialFilesWritten: false));
    }

    private Result<AuthoringSession> Open(
        string projectPath,
        string playbackPath,
        string workspaceRoot,
        string toolVersion,
        bool allowCreate)
    {
        Result<string> normalizedProject = NormalizeSource(projectPath, ".aap", "project");
        Result<string> normalizedPlayback = NormalizeSource(playbackPath, ".aas", "playback");
        Result<string> normalizedRoot = NormalizeRoot(workspaceRoot);
        if (!normalizedProject.Success || normalizedProject.Value == null
            || !normalizedPlayback.Success || normalizedPlayback.Value == null
            || !normalizedRoot.Success || normalizedRoot.Value == null)
        {
            return Result<AuthoringSession>.Fail(
                normalizedProject.Error.Length != 0
                    ? normalizedProject.Error
                    : normalizedPlayback.Error.Length != 0
                        ? normalizedPlayback.Error
                        : normalizedRoot.Error);
        }

        if (string.IsNullOrWhiteSpace(toolVersion))
        {
            return Result<AuthoringSession>.Fail("Canonical authoring tool version is required.");
        }

        Result<ProjectSnapshot> projectRead = _projectReader.Read(normalizedProject.Value);
        Result<PlaybackArchiveSnapshot> playbackRead =
            _playbackReader.Read(normalizedPlayback.Value);
        if (!projectRead.Success || projectRead.Value == null
            || !playbackRead.Success || playbackRead.Value == null)
        {
            return Result<AuthoringSession>.Fail(
                $"Source read failed: AAP={projectRead.Error}; AAS={playbackRead.Error}");
        }

        ProjectSnapshot project = projectRead.Value;
        PlaybackArchiveSnapshot playback = playbackRead.Value;
        Result<ProjectPlaybackMappingReport> mapping = _mapper.Map(project, playback);
        if (!mapping.Success || mapping.Value == null)
        {
            return Result<AuthoringSession>.Fail(
                $"Project/playback mapping failed: {mapping.Error}");
        }

        var source = new ModWorkspaceSourceBinding(
            project.Source.PathKey,
            project.Source.RevisionSha256,
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playback.SchemaName);
        var context = new ModWorkspaceRuntimeContext(
            toolVersion,
            source,
            AvailableCapabilities);
        var workspaceStore = new JsonModWorkspaceStore(normalizedRoot.Value);
        var commandStore = new JsonCommandWorkspaceStore(
            normalizedRoot.Value,
            compiler: _compiler,
            binder: new PlaybackCommandBinder());
        Result<ModWorkspaceLoadSnapshot> loaded =
            workspaceStore.TryLoad(project.Source.PathKey, context);
        if (!loaded.Success || loaded.Value == null)
        {
            return Result<AuthoringSession>.Fail(
                $"Workspace load failed: {loaded.Error}");
        }

        bool workspaceIsNew = loaded.Value.Compatibility.Status
            == ModWorkspaceCompatibilityStatus.NotFound;
        if (workspaceIsNew && !allowCreate)
        {
            return Result<AuthoringSession>.Fail(
                "No Mod workspace exists for this project.");
        }

        ModWorkspaceManifest workspace;
        CommandTimelineDocument timeline;
        if (workspaceIsNew)
        {
            string workspaceId = Guid.NewGuid().ToString("D");
            workspace = new ModWorkspaceManifest(
                ModWorkspaceManifest.CurrentSchemaVersion,
                workspaceId,
                toolVersion,
                toolVersion,
                source,
                Array.AsReadOnly(new[]
                {
                    CharacterTransformCommandFamilyCompiler.CapabilityId
                }));
            timeline = new CommandTimelineDocument(
                CommandTimelineDocument.CurrentSchemaVersion,
                workspaceId,
                project.Source.PathKey,
                project.Source.RevisionSha256,
                Array.Empty<CommandTimelineEntry>());
        }
        else
        {
            if (loaded.Value.Manifest == null
                || !loaded.Value.Compatibility.CanWrite
                || !loaded.Value.Compatibility.CanExecute)
            {
                return Result<AuthoringSession>.Fail(
                    $"Workspace is not writable: {loaded.Value.Compatibility.Status}: "
                    + loaded.Value.Compatibility.Message);
            }

            workspace = loaded.Value.Manifest;
            Result<CommandTimelineLoadSnapshot> timelineLoad =
                commandStore.TryLoadTimeline(
                    project.Source.PathKey,
                    workspace,
                    project,
                    playback);
            if (!timelineLoad.Success || timelineLoad.Value == null)
            {
                return Result<AuthoringSession>.Fail(
                    $"Command timeline load failed: {timelineLoad.Error}");
            }

            if (timelineLoad.Value.Status == CommandWorkspaceDocumentStatus.NotFound)
            {
                timeline = new CommandTimelineDocument(
                    CommandTimelineDocument.CurrentSchemaVersion,
                    workspace.WorkspaceId,
                    project.Source.PathKey,
                    project.Source.RevisionSha256,
                    Array.Empty<CommandTimelineEntry>());
            }
            else if (timelineLoad.Value.Status == CommandWorkspaceDocumentStatus.Ready
                && timelineLoad.Value.Timeline != null)
            {
                timeline = timelineLoad.Value.Timeline;
            }
            else
            {
                return Result<AuthoringSession>.Fail(
                    $"Command timeline is not editable: {timelineLoad.Value.Status}.");
            }
        }

        return Result<AuthoringSession>.Ok(new(
            project,
            playback,
            mapping.Value,
            context,
            workspace,
            timeline,
            workspaceIsNew,
            workspaceStore,
            commandStore));
    }

    private static Result<SceneKey> ResolveRecordScene(
        ProjectPlaybackMappingReport mapping,
        PlaybackArchiveSnapshot playback,
        int playbackRecordIndex)
    {
        if (playbackRecordIndex < 0
            || playbackRecordIndex >= playback.Records.Count)
        {
            return Result<SceneKey>.Fail(
                $"Playback record must be within 0..{playback.Records.Count - 1}.");
        }

        ScenePlaybackMapping[] matches = mapping.Scenes
            .Where(scene => scene.PlaybackRecordIndex == playbackRecordIndex)
            .ToArray();
        if (matches.Length != 1)
        {
            return Result<SceneKey>.Fail(
                $"Playback record {playbackRecordIndex} maps to {matches.Length} project scenes; exactly one is required.");
        }

        return Result<SceneKey>.Ok(matches[0].Scene);
    }

    private static Result<string> NormalizeSource(
        string path,
        string extension,
        string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Result<string>.Fail($"Authoring {label} path is empty.");
            }

            string fullPath = Path.GetFullPath(path.Trim());
            if (!File.Exists(fullPath)
                || !string.Equals(
                    Path.GetExtension(fullPath),
                    extension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<string>.Fail(
                    $"Authoring {label} path must be an existing {extension} file.");
            }

            return Result<string>.Ok(fullPath);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(
                $"Authoring {label} path is invalid: {ex.Message}");
        }
    }

    private static Result<string> NormalizeRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Result<string>.Fail("Authoring workspace root is empty.");
            }

            return Result<string>.Ok(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(
                $"Authoring workspace root is invalid: {ex.Message}");
        }
    }

    private static string Location(SceneKey scene) =>
        $"{scene.NodeGuid}:{scene.SceneIndex}";

    private sealed record AuthoringSession(
        ProjectSnapshot Project,
        PlaybackArchiveSnapshot Playback,
        ProjectPlaybackMappingReport Mapping,
        ModWorkspaceRuntimeContext Context,
        ModWorkspaceManifest Workspace,
        CommandTimelineDocument Timeline,
        bool WorkspaceIsNew,
        JsonModWorkspaceStore WorkspaceStore,
        JsonCommandWorkspaceStore CommandStore);
}
