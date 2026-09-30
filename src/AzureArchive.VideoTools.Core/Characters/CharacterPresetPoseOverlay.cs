namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>Managed values only. Position is deliberately absent: the existing root pivot stays fixed.</summary>
public readonly record struct CharacterPresetPose(
    float EulerX, float EulerY, float EulerZ, float ScaleX, float ScaleY, float ScaleZ);

public static class CharacterPresetPoseOverlay
{
    /// <summary>Project a logical snapshot without changing the displayed native pose.</summary>
    public static CharacterTransformState ProjectAuthoredState(CharacterTransformState current,
        CharacterPresetPose baseline, CharacterPresetPose applied, bool ownsRotation) =>
        ProjectAuthoredState(current, baseline, applied, ownsRotation, false);

    /// <summary>Yaw owns one complete orientation; tilt keeps the original Z-channel ownership.</summary>
    public static CharacterTransformState ProjectAuthoredState(CharacterTransformState current,
        CharacterPresetPose baseline, CharacterPresetPose applied, bool ownsRotation, bool ownsYaw)
    {
        CharacterVector3 euler = current.LocalEulerAngles;
        float physicalZ = CharacterScreenRotation.IsHorizontallyFlipped(euler.Y) ? -euler.Z : euler.Z;
        var pose = new CharacterPresetPose(euler.X, euler.Y, physicalZ, 1f, 1f, 1f);
        CharacterPresetPose clean = RemoveOwned(pose, baseline, applied, ownsRotation, false, ownsYaw);
        return current with
        {
            LocalEulerAngles = new CharacterVector3(clean.EulerX, clean.EulerY,
                CharacterScreenRotation.ToScreenDegrees(clean.EulerZ, clean.EulerY))
        };
    }

    public static CharacterPresetPose Apply(CharacterPresetPose baseline, CharacterPresetFrame frame) =>
        baseline with
        {
            EulerY = baseline.EulerY + frame.YawDegrees,
            EulerZ = baseline.EulerZ + (CharacterScreenRotation.IsHorizontallyFlipped(baseline.EulerY)
                ? -frame.RotationDegrees : frame.RotationDegrees),
            ScaleX = baseline.ScaleX * frame.ScaleX,
            ScaleY = baseline.ScaleY * frame.ScaleY
        };

    /// <summary>
    /// Remove only channels still owned by the overlay. An absolute tween/authoring
    /// write between callbacks is a new baseline, not a value to divide or subtract.
    /// This original overload retains the Z-channel ownership of tilt presets,
    /// including removal of the tilt when an external writer changes only X/Y.
    /// Scale ownership remains per-axis. Z scale is never owned.
    /// </summary>
    public static CharacterPresetPose RemoveOwned(
        CharacterPresetPose current, CharacterPresetPose baseline, CharacterPresetPose applied,
        bool ownsRotation, bool ownsScale) =>
        RemoveOwned(current, baseline, applied, ownsRotation, ownsScale, false);

    /// <summary>
    /// Yaw requires complete orientation ownership: Unity can express the same
    /// turn with different X/Y/Z triples. Restore all three only while the
    /// orientation is still ours. A genuinely changed external rotation wins.
    /// </summary>
    public static CharacterPresetPose RemoveOwned(
        CharacterPresetPose current, CharacterPresetPose baseline, CharacterPresetPose applied,
        bool ownsRotation, bool ownsScale, bool ownsYaw)
    {
        bool restoreRotation = ownsRotation && ownsYaw && SameRotation(current, applied);
        bool restoreTilt = ownsRotation && !ownsYaw && SameAngle(current.EulerZ, applied.EulerZ);
        return current with
        {
            EulerX = restoreRotation ? baseline.EulerX : current.EulerX,
            EulerY = restoreRotation ? baseline.EulerY : current.EulerY,
            EulerZ = restoreRotation || restoreTilt ? baseline.EulerZ : current.EulerZ,
            ScaleX = ownsScale && SameValue(current.ScaleX, applied.ScaleX)
                ? baseline.ScaleX : current.ScaleX,
            ScaleY = ownsScale && SameValue(current.ScaleY, applied.ScaleY)
                ? baseline.ScaleY : current.ScaleY
        };
    }

    public static bool IsFinite(CharacterPresetPose pose) =>
        float.IsFinite(pose.EulerX) && float.IsFinite(pose.EulerY) && float.IsFinite(pose.EulerZ)
        && float.IsFinite(pose.ScaleX) && float.IsFinite(pose.ScaleY) && float.IsFinite(pose.ScaleZ);

    /// <summary>
    /// Compare orientation rather than Euler components, with a 0.01-degree
    /// angular tolerance for the native Euler round trip. All math is managed;
    /// no native quaternion getter, boxing or wrapper retention is involved.
    /// </summary>
    public static bool SameRotation(CharacterPresetPose left, CharacterPresetPose right)
    {
        if (!FiniteRotation(left) || !FiniteRotation(right)) return false;
        var a = RotationOf(left);
        var b = RotationOf(right);
        // Unit q and -q denote one orientation. Quaternion chord distance for
        // angular separation theta is 2*sin(theta/4); use both signs so angle
        // wraps and alternative Euler triples keep the same ownership.
        double minus = Square(a.X - b.X) + Square(a.Y - b.Y)
            + Square(a.Z - b.Z) + Square(a.W - b.W);
        double plus = Square(a.X + b.X) + Square(a.Y + b.Y)
            + Square(a.Z + b.Z) + Square(a.W + b.W);
        double tolerance = 2d * Math.Sin(0.01d * Math.PI / 720d);
        return Math.Min(minus, plus) <= tolerance * tolerance;
    }

    private static bool FiniteRotation(CharacterPresetPose pose) =>
        float.IsFinite(pose.EulerX) && float.IsFinite(pose.EulerY) && float.IsFinite(pose.EulerZ);

    private static (double X, double Y, double Z, double W) RotationOf(CharacterPresetPose pose)
    {
        // Unity documents Euler's rotation order as Z, X, Y; the resulting
        // orientation is qY*qX*qZ. Unity 2023.2 documentation:
        // https://docs.unity.cn/2023.2/Documentation/ScriptReference/Quaternion.Euler.html
        double halfX = Math.IEEERemainder(pose.EulerX, 360d) * Math.PI / 360d;
        double halfY = Math.IEEERemainder(pose.EulerY, 360d) * Math.PI / 360d;
        double halfZ = Math.IEEERemainder(pose.EulerZ, 360d) * Math.PI / 360d;
        double sx = Math.Sin(halfX), cx = Math.Cos(halfX);
        double sy = Math.Sin(halfY), cy = Math.Cos(halfY);
        double sz = Math.Sin(halfZ), cz = Math.Cos(halfZ);
        return (cy * sx * cz + sy * cx * sz,
            sy * cx * cz - cy * sx * sz,
            cy * cx * sz - sy * sx * cz,
            cy * cx * cz + sy * sx * sz);
    }

    private static double Square(double value) => value * value;

    private static bool SameAngle(float a, float b) =>
        MathF.Abs(MathF.IEEERemainder(a - b, 360f)) <= .002f;

    private static bool SameValue(float a, float b) =>
        MathF.Abs(a - b) <= .00001f * MathF.Max(1e-30f, MathF.Max(MathF.Abs(a), MathF.Abs(b)));
}

/// <summary>Dialogue cleanup authority does not grant authority to start any command.</summary>
public sealed class CharacterPresetWindowGate
{
    private long _latestBoundary;

    public bool ObserveBoundary(long sequence)
    {
        if (sequence <= _latestBoundary) return false;
        _latestBoundary = sequence;
        return true;
    }

    public bool MayStart(long sequence) => sequence > 0 && sequence >= _latestBoundary;

    /// <summary>
    /// A completed native call with no compiled message and an unchanged known
    /// row is a hold/no-op, not a new dialogue. Unknown rows remain conservative.
    /// Explicit authorized dispatch still calls ObserveBoundary for same-row replay.
    /// </summary>
    public bool ObservePlaybackBoundary(long sequence, int rowBefore, int rowAfter, int messageCount) =>
        (messageCount > 0 || rowBefore < 0 || rowAfter < 0 || rowBefore != rowAfter)
        && ObserveBoundary(sequence);
}
