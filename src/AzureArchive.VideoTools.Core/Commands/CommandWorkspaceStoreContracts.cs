using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Core.Commands;

public enum CommandWorkspaceDocumentStatus
{
    Ready = 0,
    NotFound = 1,
    FutureSchema = 2,
    UnsupportedLegacySchema = 3
}

public enum CommandWorkspaceLoadSource
{
    None = 0,
    Primary = 1,
    Backup = 2
}

public sealed record CommandWorkspaceStoreNotice(
    string Code,
    string Message);

public sealed record CommandTimelineLoadSnapshot(
    CommandTimelineDocument? Timeline,
    int DetectedSchemaVersion,
    CommandWorkspaceDocumentStatus Status,
    CommandWorkspaceLoadSource Source,
    IReadOnlyList<CommandWorkspaceStoreNotice> Notices)
{
    public bool RecoveredFromBackup => Source == CommandWorkspaceLoadSource.Backup;
}

public sealed record PlaybackCommandProjectionLoadSnapshot(
    PlaybackCommandProjection? Projection,
    int DetectedSchemaVersion,
    CommandWorkspaceDocumentStatus Status,
    CommandWorkspaceLoadSource Source,
    IReadOnlyList<CommandWorkspaceStoreNotice> Notices)
{
    public bool RecoveredFromBackup => Source == CommandWorkspaceLoadSource.Backup;
}

public sealed record CommandWorkspaceSaveSnapshot(
    string FullPath,
    bool ReplacedExisting,
    bool BackupCreated);

public interface ICommandWorkspaceStore
{
    Result<CommandTimelineLoadSnapshot> TryLoadTimeline(
        string projectPathKey,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);

    Result<CommandWorkspaceSaveSnapshot> SaveTimelineAtomic(
        string projectPathKey,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);

    Result<PlaybackCommandProjectionLoadSnapshot> TryLoadProjection(
        string projectPathKey,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);

    Result<CommandWorkspaceSaveSnapshot> SaveProjectionAtomic(
        string projectPathKey,
        PlaybackCommandProjection projection,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);

    Result<CommandWorkspaceSaveSnapshot> CompileAndSaveProjectionAtomic(
        string projectPathKey,
        CommandTimelineDocument timeline,
        ModWorkspaceManifest workspace,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);
}
