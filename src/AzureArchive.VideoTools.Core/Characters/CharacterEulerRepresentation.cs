namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// Chooses a nearby Z-X-Y Euler representation of an actually observed orientation.
/// Unity may exchange Y/Z half turns when X passes 90 degrees. This operation
/// changes only the equivalent representation, never the observed orientation.
/// </summary>
public static class CharacterEulerRepresentation
{
    public static CharacterVector3 NearestEquivalent(CharacterVector3 observed, CharacterVector3 reference)
    {
        if (!observed.IsFinite || !reference.IsFinite) return observed;
        if (SameRotation(observed, reference)) return reference;

        CharacterVector3 first = Near(observed, reference);
        CharacterVector3 second = Near(new CharacterVector3(
            180f - observed.X, observed.Y + 180f, observed.Z + 180f), reference);
        // Large scalar references may lose precision when adding a nearby
        // turn. Every alternative, including the ordinary wrapped form, must
        // prove equivalence to the actual measurement before it is returned.
        CharacterVector3 result = SameRotation(observed, first) ? first : observed;
        if (Distance(second, reference) < Distance(result, reference) && SameRotation(observed, second)) result = second;

        // At a pitch pole Unity may collapse yaw/tilt into one angle. Keep the
        // closest representative satisfying the observed coupled angle, and
        // accept it only after the managed orientation comparison succeeds.
        float signedX = MathF.IEEERemainder(observed.X, 360f);
        if (MathF.Abs(MathF.Abs(signedX) - 90f) <= .0001f)
        {
            bool positivePole = signedX > 0f;
            float observedCoupled = positivePole ? observed.Y - observed.Z : observed.Y + observed.Z;
            float referenceCoupled = positivePole ? reference.Y - reference.Z : reference.Y + reference.Z;
            float correction = MathF.IEEERemainder(observedCoupled - referenceCoupled, 360f) / 2f;
            var pole = new CharacterVector3(
                CharacterAngleMath.NearestPathFromCurrent(observed.X, reference.X),
                reference.Y + correction,
                reference.Z + (positivePole ? -correction : correction));
            if (SameRotation(observed, pole) && Distance(pole, reference) < Distance(result, reference)) result = pole;
        }

        return result;
    }

    private static CharacterVector3 Near(CharacterVector3 value, CharacterVector3 reference) => new(
        CharacterAngleMath.NearestPathFromCurrent(value.X, reference.X),
        CharacterAngleMath.NearestPathFromCurrent(value.Y, reference.Y),
        CharacterAngleMath.NearestPathFromCurrent(value.Z, reference.Z));

    private static double Distance(CharacterVector3 value, CharacterVector3 reference) =>
        Square((double)value.X - reference.X) + Square((double)value.Y - reference.Y) + Square((double)value.Z - reference.Z);

    private static double Square(double value) => value * value;

    private static bool SameRotation(CharacterVector3 left, CharacterVector3 right) =>
        CharacterPresetPoseOverlay.SameRotation(
            new CharacterPresetPose(left.X, left.Y, left.Z, 1f, 1f, 1f),
            new CharacterPresetPose(right.X, right.Y, right.Z, 1f, 1f, 1f));
}
