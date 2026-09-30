using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Core.Commands;

public enum CommandTimelinePhase
{
    SceneEnter = 0
}

public sealed record AuthoringCommand(
    string CommandId,
    int Order,
    CommandTimelinePhase Phase,
    string CommandType,
    string Directive,
    bool Enabled = true);

public sealed record CommandTimelineEntry(
    SceneKey Scene,
    IReadOnlyList<AuthoringCommand> Commands);

public sealed record CommandTimelineDocument(
    int SchemaVersion,
    string WorkspaceId,
    string ProjectPathKey,
    string ProjectRevisionSha256,
    IReadOnlyList<CommandTimelineEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CompiledScriptIdentity(
    string Sha256,
    int Utf16Length,
    int LineCount);

public sealed record CanonicalTimelineCommand(
    string CommandType,
    string RequiredCapability,
    string Directive,
    int PublicSlot);

public sealed record PlaybackCommandInstruction(
    string CommandId,
    int Order,
    CommandTimelinePhase Phase,
    string CommandType,
    string RequiredCapability,
    string CanonicalDirective);

public sealed record PlaybackCommandBatch(
    SceneKey Scene,
    int PlaybackRecordIndex,
    string PlaybackRecordFingerprint,
    CompiledScriptIdentity CompiledScript,
    IReadOnlyList<PlaybackCommandInstruction> Commands);

public sealed record PlaybackCommandProjection(
    int SchemaVersion,
    string WorkspaceId,
    string SourceTimelineSha256,
    string ProjectPathKey,
    string ProjectRevisionSha256,
    string PlaybackPathKey,
    string PlaybackRevisionSha256,
    string PlaybackSchemaName,
    IReadOnlyList<PlaybackCommandBatch> Batches)
{
    public const int CurrentSchemaVersion = 1;
}

public interface ICommandFamilyCompiler
{
    string CommandType { get; }

    string RequiredCapability { get; }

    Result<CanonicalTimelineCommand> Canonicalize(string directive);
}

public interface ICommandTimelineCompiler
{
    Result<PlaybackCommandProjection> Compile(
        ProjectSnapshot project,
        PlaybackArchiveSnapshot playback,
        ModWorkspaceManifest workspace,
        CommandTimelineDocument timeline);
}

public enum PlaybackCommandObservationStatus
{
    NotFound = 0,
    Ambiguous = 1,
    OrdinaryRecord = 2,
    CommandBatch = 3
}

public sealed record PlaybackCommandObservation(
    PlaybackCommandObservationStatus Status,
    int? PlaybackRecordIndex,
    string? PlaybackRecordFingerprint,
    PlaybackCommandBatch? Batch,
    IReadOnlyList<int> CandidatePlaybackRecordIndices);

public interface IPlaybackCommandIndex
{
    string PlaybackPathKey { get; }

    string PlaybackRevisionSha256 { get; }

    int RecordCount { get; }

    int BatchCount { get; }

    Result<PlaybackCommandObservation> Observe(
        ObservedCompiledSceneIdentity identity);
}

public interface IPlaybackCommandBinder
{
    Result<IPlaybackCommandIndex> Bind(
        PlaybackArchiveSnapshot playback,
        PlaybackCommandProjection? projection);
}
