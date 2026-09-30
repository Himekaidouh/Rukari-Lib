using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

public enum SceneCameraOperation
{
    Set = 0,
    Move = 1,
    Reset = 2
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
    CharacterTransformEasing Easing);

public readonly record struct SceneCameraState(float X, float Y, float Zoom)
{
    public static SceneCameraState Default => new(0f, 0f, 1f);

    public bool IsFinite =>
        float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Zoom);
}

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
