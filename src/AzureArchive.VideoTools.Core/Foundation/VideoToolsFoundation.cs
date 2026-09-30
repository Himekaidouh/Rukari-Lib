using AzureArchive.VideoTools.Core.Compilation;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Continuity;
using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Workspaces;

namespace AzureArchive.VideoTools.Core.Foundation;

public interface IVideoToolsFoundation
{
    IProjectReader Projects { get; }

    IPlaybackArchiveReader PlaybackArchives { get; }

    IContinuationEngine Continuity { get; }

    IContinuationReconciler Reconciler { get; }

    IProjectPlaybackMapper PlaybackMapper { get; }

    IObservedSceneIdentityResolver SceneIdentityResolver { get; }

    IContinuationProjectionCompiler ProjectionCompiler { get; }

    IPlaybackProjectionBinder PlaybackProjectionBinder { get; }

    IContinuationStore ContinuationStore { get; }

    IContinuationProjectionStore ProjectionStore { get; }

    IModWorkspaceStore Workspaces { get; }

    ICommandTimelineCompiler CommandTimelineCompiler { get; }

    IPlaybackCommandBinder PlaybackCommandBinder { get; }

    ICommandWorkspaceStore CommandWorkspace { get; }
}

public sealed class VideoToolsFoundation : IVideoToolsFoundation
{
    public VideoToolsFoundation(
        IProjectReader projects,
        IPlaybackArchiveReader playbackArchives,
        IContinuationStore continuationStore,
        IContinuationProjectionStore projectionStore,
        IModWorkspaceStore workspaceStore,
        ICommandWorkspaceStore commandWorkspaceStore,
        IContinuationEngine? continuity = null,
        IContinuationReconciler? reconciler = null,
        IProjectPlaybackMapper? playbackMapper = null,
        IContinuationProjectionCompiler? projectionCompiler = null,
        IPlaybackProjectionBinder? playbackProjectionBinder = null,
        IObservedSceneIdentityResolver? sceneIdentityResolver = null,
        ICommandTimelineCompiler? commandTimelineCompiler = null,
        IPlaybackCommandBinder? playbackCommandBinder = null)
    {
        Projects = projects ?? throw new ArgumentNullException(nameof(projects));
        PlaybackArchives = playbackArchives
            ?? throw new ArgumentNullException(nameof(playbackArchives));
        ContinuationStore = continuationStore
            ?? throw new ArgumentNullException(nameof(continuationStore));
        ProjectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        Workspaces = workspaceStore
            ?? throw new ArgumentNullException(nameof(workspaceStore));
        CommandWorkspace = commandWorkspaceStore
            ?? throw new ArgumentNullException(nameof(commandWorkspaceStore));
        Continuity = continuity ?? new ContinuationEngine();
        Reconciler = reconciler ?? new ContinuationReconciler();
        PlaybackMapper = playbackMapper ?? new ConservativeProjectPlaybackMapper();
        SceneIdentityResolver = sceneIdentityResolver
            ?? new ObservedSceneIdentityResolver(PlaybackMapper);
        ProjectionCompiler = projectionCompiler
            ?? new ContinuationProjectionCompiler(Continuity, PlaybackMapper);
        PlaybackProjectionBinder = playbackProjectionBinder
            ?? new PlaybackProjectionBinder();
        CommandTimelineCompiler = commandTimelineCompiler
            ?? new CommandTimelineCompiler(playbackMapper: PlaybackMapper);
        PlaybackCommandBinder = playbackCommandBinder
            ?? new PlaybackCommandBinder();
    }

    public IProjectReader Projects { get; }

    public IPlaybackArchiveReader PlaybackArchives { get; }

    public IContinuationEngine Continuity { get; }

    public IContinuationReconciler Reconciler { get; }

    public IProjectPlaybackMapper PlaybackMapper { get; }

    public IObservedSceneIdentityResolver SceneIdentityResolver { get; }

    public IContinuationProjectionCompiler ProjectionCompiler { get; }

    public IPlaybackProjectionBinder PlaybackProjectionBinder { get; }

    public IContinuationStore ContinuationStore { get; }

    public IContinuationProjectionStore ProjectionStore { get; }

    public IModWorkspaceStore Workspaces { get; }

    public ICommandTimelineCompiler CommandTimelineCompiler { get; }

    public IPlaybackCommandBinder PlaybackCommandBinder { get; }

    public ICommandWorkspaceStore CommandWorkspace { get; }
}
