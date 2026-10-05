using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public static class CharacterCoordinateSpace
{
    public const float ScreenWidth = 2960f;
    public const float VisibleLeft = -1480f;
    public const float VisibleRight = 1480f;
}

public enum CharacterTransformOperation
{
    Set = 0,
    Move = 1,
    Reset = 2
}

public enum CharacterTransformEasing
{
    Linear = 0,
    EaseIn = 1,
    EaseOut = 2,
    EaseInOut = 3
}

public readonly record struct CharacterVector3(float X, float Y, float Z)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}

public sealed record CharacterTransformCommand(
    int PublicSlot,
    CharacterTransformOperation Operation,
    float? X,
    float? Y,
    float? DeltaX,
    float? DeltaY,
    float? RotationDegrees,
    float? DeltaRotationDegrees,
    bool? FlipX,
    int DurationMilliseconds,
    CharacterTransformEasing Easing)
{
    /// <summary>Local X-axis pitch. The original RotationDegrees remains screen-space Z tilt.</summary>
    public float? RotationXDegrees { get; init; }

    /// <summary>Relative local X-axis pitch, independent of horizontal facing.</summary>
    public float? DeltaRotationXDegrees { get; init; }
}

/// <summary>
/// Managed character transform snapshot. Position follows the live character
/// Transform; local X/Y use a verified equivalent Euler representation of its
/// measured orientation. LocalEulerAngles.Z is intentionally the
/// tilt visible on screen: its sign is compensated across a horizontal Y-axis
/// half turn and is not the raw Unity local Z value.
/// </summary>
public sealed record CharacterTransformState(
    CharacterVector3 Position,
    CharacterVector3 LocalEulerAngles);

public sealed record CharacterTransformBaseline(
    string SceneIdentity,
    int PublicSlot,
    string OccupantIdentifier,
    CharacterTransformState State);

public sealed record CharacterTransformTarget(
    CharacterTransformState State,
    bool PositionChanged,
    bool RotationChanged,
    int DurationMilliseconds,
    CharacterTransformEasing Easing,
    bool IsReset);

public interface ICharacterTransformDirectiveParser
{
    Result<CharacterTransformCommand> Parse(string directive);
}

public interface ICharacterTransformPlanner
{
    Result<CharacterTransformTarget> Plan(
        CharacterTransformCommand command,
        CharacterTransformState current,
        CharacterTransformBaseline baseline);
}

public interface ICharacterTransformBaselineStore
{
    Result<CharacterTransformBaseline> Capture(
        string sceneIdentity,
        int publicSlot,
        string occupantIdentifier,
        CharacterTransformState state);

    Result<CharacterTransformBaseline> Get(
        string sceneIdentity,
        int publicSlot,
        string occupantIdentifier);

    void Clear();
}
