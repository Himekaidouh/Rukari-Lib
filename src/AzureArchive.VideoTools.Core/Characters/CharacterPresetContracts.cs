using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public enum CharacterPresetKind
{
    Sway = 0,
    Spin = 1,
    Headbutt = 2,
    Squash = 3
}

/// <summary>
/// A temporary offset from the character's current authored pose. Angular values
/// are in degrees; squash amplitude is logarithmic. Cycles=0 runs until the next
/// dialogue boundary. Headbutt uses only its angles, direction and duration.
/// Direction +1 is right: negative screen Z for leaning, positive Y yaw for
/// turning in place. Direction -1 is left.
/// </summary>
public sealed record CharacterPresetCommand(
    int PublicSlot,
    CharacterPresetKind Kind,
    float Amplitude,
    float FrequencyHz,
    int Cycles,
    int Direction,
    float BackDegrees,
    float ForwardDegrees,
    int DurationMilliseconds)
{
    /// <summary>
    /// Optional timing curve for each cycle (or each headbutt stage).
    /// Null preserves the preset's original timing exactly.
    /// </summary>
    public CharacterPresetBezier? Bezier { get; init; }
}

/// <summary>
/// Relative screen tilt, Y-axis yaw and positive multipliers for the existing
/// X/Y scale. The original four-argument constructor remains unchanged.
/// A completed frame is always exactly neutral on both rotation axes.
/// </summary>
public readonly record struct CharacterPresetFrame(
    float RotationDegrees,
    float ScaleX,
    float ScaleY,
    bool Completed)
{
    /// <summary>Relative Y-axis turn in degrees; zero for non-spin presets.</summary>
    public float YawDegrees { get; init; }

    public static CharacterPresetFrame Neutral(bool completed = false) =>
        new(0f, 1f, 1f, completed);
}

public interface ICharacterPresetDirectiveParser
{
    Result<CharacterPresetCommand> Parse(string directive);
}
