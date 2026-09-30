namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// Converts between Unity's local Z Euler angle and the tilt visible on screen.
/// A Y-axis half turn mirrors the local Z direction, while AAVT directives keep
/// their rotation sign stable in screen space.
/// </summary>
public static class CharacterScreenRotation
{
    public static bool IsHorizontallyFlipped(float localEulerY)
    {
        float signedY = NormalizeSigned(localEulerY);
        return MathF.Abs(signedY) > 90f;
    }

    public static float ToScreenDegrees(float physicalLocalEulerZ, float physicalLocalEulerY)
    {
        float signedZ = NormalizeSigned(physicalLocalEulerZ);
        return IsHorizontallyFlipped(physicalLocalEulerY) ? -signedZ : signedZ;
    }

    public static float ToPhysicalDegrees(
        float screenDegrees,
        float targetLocalEulerY,
        float currentPhysicalLocalEulerZ)
    {
        float target = IsHorizontallyFlipped(targetLocalEulerY)
            ? -screenDegrees
            : screenDegrees;
        return CharacterAngleMath.NearestEquivalent(target, currentPhysicalLocalEulerZ);
    }

    /// <summary>
    /// Dispatch-time conversion that preserves the commanded tween path.
    /// Deliberate multi-turn spins (|degrees| &gt; 360) keep their full raw
    /// magnitude instead of collapsing to the nearest equivalent pose, and
    /// ordinary poses take the shortest arc from the LIVE value without
    /// normalizing it first (so a live -10 tweening to 25 travels 35 degrees,
    /// not 395). The mirrored sign for flipped characters still applies.
    /// </summary>
    public static float ToPhysicalDegreesFromLive(
        float screenDegrees,
        float targetLocalEulerY,
        float currentPhysicalLocalEulerZ)
    {
        float signed = IsHorizontallyFlipped(targetLocalEulerY)
            ? -screenDegrees
            : screenDegrees;
        if (CharacterAngleMath.IsDeliberateMultiTurn(screenDegrees))
        {
            return signed;
        }

        float delta = CharacterAngleMath.Normalize360(
            CharacterAngleMath.Normalize360(signed)
            - CharacterAngleMath.Normalize360(currentPhysicalLocalEulerZ));
        if (delta >= CharacterAngleMath.HalfTurnDegrees)
        {
            delta -= CharacterAngleMath.FullTurnDegrees;
        }

        return currentPhysicalLocalEulerZ + delta;
    }

    private static float NormalizeSigned(float value)
    {
        float normalized = CharacterAngleMath.Normalize360(value);
        return normalized > 180f ? normalized - 360f : normalized;
    }
}
