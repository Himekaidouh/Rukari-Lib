using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Compilation;

public sealed record ContinuationPlaybackInstruction(
    SceneKey Scene,
    SceneKey PreviousScene,
    SceneKey ChainStartScene,
    int PlaybackRecordIndex,
    string PlaybackRecordFingerprint,
    int PreviousPlaybackRecordIndex,
    int ChainStartPlaybackRecordIndex,
    string ExpectedLockedPrefix,
    string Suffix,
    string ResultText,
    int TypewriterStartCharacterIndex);

public sealed record ContinuationPlaybackProjection(
    int SchemaVersion,
    string ProjectId,
    string ProjectPathKey,
    string ProjectRevisionSha256,
    string PlaybackPathKey,
    string PlaybackRevisionSha256,
    string PlaybackSchemaName,
    IReadOnlyList<ContinuationPlaybackInstruction> Instructions)
{
    public const int CurrentSchemaVersion = 1;
}

public interface IContinuationProjectionCompiler
{
    Result<ContinuationPlaybackProjection> Compile(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        ContinuationDocument metadata);
}

public enum ContinuationProjectionLoadSource
{
    None = 0,
    Primary = 1,
    Backup = 2
}

public sealed record ContinuationProjectionStoreNotice(
    string Code,
    string Message);

public sealed record ContinuationProjectionLoadSnapshot(
    ContinuationPlaybackProjection? Projection,
    ContinuationProjectionLoadSource Source,
    IReadOnlyList<ContinuationProjectionStoreNotice> Notices)
{
    public bool RecoveredFromBackup =>
        Source == ContinuationProjectionLoadSource.Backup;
}

public sealed record ContinuationProjectionSaveSnapshot(
    string FullPath,
    bool ReplacedExisting,
    bool BackupCreated);

public interface IContinuationProjectionStore
{
    Result<ContinuationProjectionLoadSnapshot> TryLoad(
        string sidecarPath,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);

    Result<ContinuationProjectionSaveSnapshot> SaveAtomic(
        string sidecarPath,
        ContinuationPlaybackProjection projection,
        ProjectSnapshot currentProject,
        PlaybackArchiveSnapshot currentPlayback);
}
