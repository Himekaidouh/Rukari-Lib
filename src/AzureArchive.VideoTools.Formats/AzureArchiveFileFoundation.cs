using AzureArchive.VideoTools.Core.Foundation;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using AzureArchive.VideoTools.Formats.Sidecar;
using AzureArchive.VideoTools.Formats.Workspace;

namespace AzureArchive.VideoTools.Formats;

public static class AzureArchiveFileFoundation
{
    public static IVideoToolsFoundation Create(
        string sidecarRootDirectory,
        AapProjectReaderOptions? projectReaderOptions = null,
        AasScenarioReaderOptions? playbackReaderOptions = null,
        JsonContinuationStoreOptions? continuationStoreOptions = null,
        JsonContinuationProjectionStoreOptions? projectionStoreOptions = null,
        string? workspaceRootDirectory = null,
        JsonModWorkspaceStoreOptions? workspaceStoreOptions = null,
        JsonCommandWorkspaceStoreOptions? commandWorkspaceStoreOptions = null)
    {
        string workspaceRoot = workspaceRootDirectory
            ?? Path.Combine(sidecarRootDirectory, "workspaces");
        var playbackMapper = new ConservativeProjectPlaybackMapper();
        var commandCompiler = new CommandTimelineCompiler(
            playbackMapper: playbackMapper);
        var commandBinder = new PlaybackCommandBinder();
        var commandStore = new JsonCommandWorkspaceStore(
            workspaceRoot,
            commandWorkspaceStoreOptions,
            commandCompiler,
            commandBinder);
        return new VideoToolsFoundation(
            new AapProjectReader(projectReaderOptions),
            new AasScenarioReader(playbackReaderOptions),
            new JsonContinuationStore(sidecarRootDirectory, continuationStoreOptions),
            new JsonContinuationProjectionStore(
                sidecarRootDirectory,
                projectionStoreOptions),
            new JsonModWorkspaceStore(
                workspaceRoot,
                workspaceStoreOptions),
            commandStore,
            playbackMapper: playbackMapper,
            commandTimelineCompiler: commandCompiler,
            playbackCommandBinder: commandBinder);
    }
}
