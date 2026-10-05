using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Scene camera state a scene inherits from its ancestors.</summary>
public sealed record PreviewCameraChainResolution(
    SceneCameraState InheritedState,
    int FoldedCommandCount,
    PreviewChainStopReason StopReason,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>True when at least one ancestor camera command was folded. A
    /// scene without any ancestor camera command must keep the official camera
    /// state untouched.</summary>
    public bool HasInheritedStart => FoldedCommandCount != 0;

    public SceneCameraState BackgroundInheritedState { get; init; } = SceneCameraState.Default;
    public int OverallFoldedCommandCount { get; init; } = FoldedCommandCount;
    public int BackgroundFoldedCommandCount { get; init; }
    public SceneCameraComposition InheritedComposition => new(InheritedState, BackgroundInheritedState);
    public bool HasOverallInheritedStart => OverallFoldedCommandCount != 0;
    public bool HasBackgroundInheritedStart => BackgroundFoldedCommandCount != 0;
}

/// <summary>
/// Folds ancestor <c>#aavt;camera;</c> directives into the composition a scene
/// already shows in sequential playback (2026-09-18).
/// <para>
/// Playback inherits the camera for free: the Back/Spine layers survive across
/// records and nothing resets them, so a scene without a camera directive keeps
/// the previous composition. The editor preview rebuilds the scene on every
/// click and <c>SceneCameraService.EnsurePhysicalBaselines</c> resets
/// managed composition whenever either layer instance changes, so the same
/// scene previewed from a default composition. This resolver reproduces the
/// playback state statically from the project graph.
/// </para>
/// <para>
/// Boundaries follow the character preview chain (project start, ambiguous
/// incoming, cycle guard, depth limit) but deliberately NOT its character
/// rules: occupant changes and official position moves do not stop the camera,
/// because neither affects the live camera during playback. A scene without a
/// camera directive is folded straight through, exactly like playback keeps the
/// live state across a scene that never touches the camera.
/// </para>
/// </summary>
public sealed class PreviewCameraChainResolver
{
    private const int MaximumWalkDepth = 512;
    private const string FoldBaselineIdentity = "preview-camera-chain/v1";

    private readonly StoryGraphPredecessorResolver _graph;
    private readonly SceneCameraDirectiveParser _parser = new();
    private readonly SceneCameraPlanner _planner = new();
    private readonly SceneCameraBaseline _foldBaseline = new(
        FoldBaselineIdentity,
        SceneCameraState.Default);

    public PreviewCameraChainResolver(ProjectSnapshot project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _graph = new StoryGraphPredecessorResolver(project);
    }

    public Result<PreviewCameraChainResolution> Resolve(
        SceneKey sceneKey,
        Func<SceneKey, string?> sceneCameraLookup)
    {
        ArgumentNullException.ThrowIfNull(sceneCameraLookup);
        return Resolve(sceneKey, (key, scope) => scope == SceneCameraScope.Overall
            ? sceneCameraLookup(key) : null);
    }

    public Result<PreviewCameraChainResolution> Resolve(
        SceneKey sceneKey,
        Func<SceneKey, SceneCameraScope, string?> sceneCameraLookup)
    {
        ArgumentNullException.ThrowIfNull(sceneKey);
        ArgumentNullException.ThrowIfNull(sceneCameraLookup);

        // Ancestors are visited newest-first, so directives are collected here
        // and folded in reverse to preserve chronological order: set/move/reset
        // must compose in the order the player would have executed them.
        var collectedDirectives = new List<(SceneCameraScope Scope, string Directive)>();
        var diagnostics = new List<string>();
        var visitedScenes = new HashSet<SceneKey> { sceneKey };
        SceneKey cursor = sceneKey;
        PreviewChainStopReason stopReason = PreviewChainStopReason.ProjectStart;
        bool walkCompleted = false;
        for (int depth = 0; depth < MaximumWalkDepth; depth++)
        {
            Result<StoryPredecessorResolution> predecessor =
                _graph.ResolvePredecessor(cursor.NodeGuid, cursor.SceneIndex);
            if (!predecessor.Success || predecessor.Value == null)
            {
                diagnostics.Add(predecessor.Error);
                stopReason = PreviewChainStopReason.CycleGuard;
                walkCompleted = true;
                break;
            }

            switch (predecessor.Value.Status)
            {
                case StoryPredecessorStatus.ProjectStart:
                    stopReason = PreviewChainStopReason.ProjectStart;
                    walkCompleted = true;
                    goto FoldAndReturn;
                case StoryPredecessorStatus.AmbiguousIncoming:
                    diagnostics.Add($"incoming-node-count={predecessor.Value.IncomingNodeCount}");
                    stopReason = PreviewChainStopReason.AmbiguousGraph;
                    walkCompleted = true;
                    goto FoldAndReturn;
            }

            SceneKey ancestorKey = predecessor.Value.Predecessor!;
            if (!visitedScenes.Add(ancestorKey))
            {
                stopReason = PreviewChainStopReason.CycleGuard;
                walkCompleted = true;
                goto FoldAndReturn;
            }

            // The list is reversed below. Collect background before overall so
            // scopes of the same ancestor fold in a stable overall-first order.
            foreach (SceneCameraScope scope in new[] { SceneCameraScope.Background, SceneCameraScope.Overall })
            {
                string? directive = sceneCameraLookup(ancestorKey, scope);
                if (!string.IsNullOrEmpty(directive)) collectedDirectives.Add((scope, directive));
            }

            cursor = ancestorKey;
        }

        if (!walkCompleted)
        {
            stopReason = PreviewChainStopReason.DepthLimit;
        }

        FoldAndReturn:
        SceneCameraComposition composition = SceneCameraComposition.Default;
        int foldedCommands = 0;
        int overallCommands = 0;
        int backgroundCommands = 0;
        for (int index = collectedDirectives.Count - 1; index >= 0; index--)
        {
            var collected = collectedDirectives[index];
            Result<SceneCameraCommand> parsed = _parser.Parse(collected.Directive);
            if (!parsed.Success || parsed.Value == null)
            {
                // Fail closed: a composition built from a partially folded chain
                // would silently show the wrong framing.
                return Result<PreviewCameraChainResolution>.Fail(
                    $"An ancestor scene camera directive could not be parsed: {parsed.Error}");
            }

            if (parsed.Value.Scope != collected.Scope)
                return Result<PreviewCameraChainResolution>.Fail("An ancestor camera lookup returned another scope.");

            Result<SceneCameraTarget> planned = _planner.Plan(
                parsed.Value, composition.ForScope(collected.Scope), _foldBaseline);
            if (!planned.Success || planned.Value == null)
            {
                return Result<PreviewCameraChainResolution>.Fail(
                    $"An ancestor scene camera directive could not be folded: {planned.Error}");
            }

            composition = composition.WithScope(collected.Scope, planned.Value.State);
            Result validation = SceneCameraCommandValidator.ValidateComposition(composition);
            if (!validation.Success)
                return Result<PreviewCameraChainResolution>.Fail(
                    "An ancestor camera composition is invalid: " + validation.Error);
            foldedCommands++;
            if (collected.Scope == SceneCameraScope.Overall) overallCommands++;
            else backgroundCommands++;
        }

        return Result<PreviewCameraChainResolution>.Ok(new(
            composition.Overall,
            foldedCommands,
            stopReason,
            Array.AsReadOnly(diagnostics.ToArray()))
        {
            BackgroundInheritedState = composition.Background,
            OverallFoldedCommandCount = overallCommands,
            BackgroundFoldedCommandCount = backgroundCommands
        });
    }
}
