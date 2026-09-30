namespace AzureArchive.VideoTools.Core.Characters;

internal static class CharacterAngleMath
{
    public const float HalfTurnDegrees = 180f;
    public const float FullTurnDegrees = 360f;

    /// <summary>Commands beyond a full turn are deliberate spins
    /// (somersaults): the tween must traverse the whole commanded path
    /// instead of collapsing to the nearest equivalent pose.</summary>
    public const float MultiTurnThresholdDegrees = 360f;

    public static bool IsDeliberateMultiTurn(float degrees) =>
        Math.Abs(degrees) > MultiTurnThresholdDegrees;

    public static float Normalize360(float value)
    {
        float normalized = value % FullTurnDegrees;
        return normalized < 0f ? normalized + FullTurnDegrees : normalized;
    }

    public static float NearestEquivalent(float target, float current)
    {
        float delta = Normalize360(Normalize360(target) - Normalize360(current));
        if (delta >= HalfTurnDegrees)
        {
            delta -= FullTurnDegrees;
        }

        return Normalize360(current) + delta;
    }

    /// <summary>
    /// Pose-style target: the commanded angle re-expressed as the equivalent
    /// angle nearest to the live current value. Unlike
    /// <see cref="NearestEquivalent"/> the current value is never normalized
    /// first, so a transform that legitimately exceeds ±360 (after deliberate
    /// spins) keeps tween continuity and the shortest path never gains a
    /// spurious extra revolution.
    /// </summary>
    public static float NearestPathFromCurrent(float commanded, float current)
    {
        float delta = Normalize360(Normalize360(commanded) - Normalize360(current));
        if (delta >= HalfTurnDegrees)
        {
            delta -= FullTurnDegrees;
        }

        return current + delta;
    }
}
