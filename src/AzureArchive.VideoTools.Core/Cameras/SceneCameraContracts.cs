using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

public enum SceneCameraOperation
{
    Set = 0,
    Move = 1,
    Reset = 2
}

public enum SceneCameraScope
{
    Overall = 0,
    Background = 1
}

public sealed record SceneCameraCommand(
    SceneCameraOperation Operation,
    float? X,
    float? Y,
    float? DeltaX,
    float? DeltaY,
    float? Zoom,
    float? DeltaZoom,
    int DurationMilliseconds,
    CharacterTransformEasing Easing)
{
    // Keep the original positional constructor and deconstruction ABI.
    public SceneCameraScope Scope { get; init; } = SceneCameraScope.Overall;
}

public readonly record struct SceneCameraState(float X, float Y, float Zoom)
{
    public static SceneCameraState Default => new(0f, 0f, 1f);

    public bool IsFinite =>
        float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Zoom);
}

public readonly record struct SceneCameraComposition(
    SceneCameraState Overall,
    SceneCameraState Background)
{
    public static SceneCameraComposition Default => new(
        SceneCameraState.Default, SceneCameraState.Default);

    public SceneCameraState ForScope(SceneCameraScope scope) => scope switch
    {
        SceneCameraScope.Overall => Overall,
        SceneCameraScope.Background => Background,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public SceneCameraComposition WithScope(SceneCameraScope scope, SceneCameraState state) => scope switch
    {
        SceneCameraScope.Overall => this with { Overall = state },
        SceneCameraScope.Background => this with { Background = state },
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };
}

/// <summary>Offset subtracted from a physical layer's original local position.</summary>
public readonly record struct SceneCameraLayerComposition(float OffsetX, float OffsetY, float Zoom);

public sealed record SceneCameraComposedLayers(
    SceneCameraLayerComposition Back,
    SceneCameraLayerComposition Spine);

public readonly record struct SceneCameraPhysicalWrites(
    bool BackPosition, bool BackScale, bool SpinePosition, bool SpineScale);

public sealed record SceneCameraBaseline(
    string SceneIdentity,
    SceneCameraState State);

public sealed record SceneCameraTarget(
    SceneCameraState State,
    bool PositionChanged,
    bool ZoomChanged,
    int DurationMilliseconds,
    CharacterTransformEasing Easing,
    bool IsReset);

public interface ISceneCameraDirectiveParser
{
    Result<SceneCameraCommand> Parse(string directive);
}

public interface ISceneCameraPlanner
{
    Result<SceneCameraTarget> Plan(
        SceneCameraCommand command,
        SceneCameraState current,
        SceneCameraBaseline baseline);

    Result<SceneCameraTarget> PlanReplayRestore(
        SceneCameraCommand command,
        SceneCameraState current,
        SceneCameraBaseline baseline);
}
