using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public sealed class CharacterTransformPlanner : ICharacterTransformPlanner
{
    public Result<CharacterTransformTarget> Plan(
        CharacterTransformCommand command,
        CharacterTransformState current,
        CharacterTransformBaseline baseline)
    {
        Result validation = CharacterTransformCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return Result<CharacterTransformTarget>.Fail(validation.Error);
        }

        if (!current.Position.IsFinite || !current.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformTarget>.Fail("Current character transform is invalid.");
        }

        if (!BaselineMatches(command, baseline, out string mismatch))
        {
            return Result<CharacterTransformTarget>.Fail(mismatch);
        }

        if (!baseline.State.Position.IsFinite || !baseline.State.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformTarget>.Fail("Character transform baseline is invalid.");
        }

        CharacterTransformState targetState;
        bool positionChanged;
        bool rotationChanged;
        bool isReset = command.Operation == CharacterTransformOperation.Reset;

        switch (command.Operation)
        {
            case CharacterTransformOperation.Set:
                targetState = PlanSet(command, current, baseline);
                positionChanged = command.X.HasValue || command.Y.HasValue;
                rotationChanged = command.RotationDegrees.HasValue
                    || command.RotationXDegrees.HasValue || command.FlipX.HasValue;
                break;
            case CharacterTransformOperation.Move:
                targetState = PlanMove(command, current);
                positionChanged = command.DeltaX.HasValue || command.DeltaY.HasValue;
                rotationChanged = command.DeltaRotationDegrees.HasValue || command.DeltaRotationXDegrees.HasValue;
                break;
            case CharacterTransformOperation.Reset:
                targetState = AdjustToNearestEquivalent(baseline.State, current);
                positionChanged = true;
                rotationChanged = true;
                break;
            default:
                return Result<CharacterTransformTarget>.Fail("Unknown character transform operation.");
        }

        return Result<CharacterTransformTarget>.Ok(new CharacterTransformTarget(
            targetState,
            positionChanged,
            rotationChanged,
            command.DurationMilliseconds,
            command.Easing,
            isReset));
    }

    public Result<CharacterTransformTarget> PlanReplayRestore(
        CharacterTransformCommand command,
        CharacterTransformState current,
        CharacterTransformBaseline baseline)
    {
        Result validation = CharacterTransformCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return Result<CharacterTransformTarget>.Fail(validation.Error);
        }

        if (!current.Position.IsFinite || !current.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformTarget>.Fail("Current character transform is invalid.");
        }

        if (!BaselineMatches(command, baseline, out string mismatch))
        {
            return Result<CharacterTransformTarget>.Fail(mismatch);
        }

        if (!baseline.State.Position.IsFinite || !baseline.State.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformTarget>.Fail("Character transform baseline is invalid.");
        }

        bool resetAll = command.Operation == CharacterTransformOperation.Reset;
        bool restoreX = resetAll || command.X.HasValue || command.DeltaX.HasValue;
        bool restoreY = resetAll || command.Y.HasValue || command.DeltaY.HasValue;
        bool restoreRotationX = resetAll || command.RotationXDegrees.HasValue || command.DeltaRotationXDegrees.HasValue;
        bool restoreRotationY = resetAll || command.FlipX.HasValue;
        bool restoreRotationZ = resetAll
            || command.RotationDegrees.HasValue
            || command.DeltaRotationDegrees.HasValue;

        var restored = new CharacterTransformState(
            new CharacterVector3(
                restoreX ? baseline.State.Position.X : current.Position.X,
                restoreY ? baseline.State.Position.Y : current.Position.Y,
                resetAll ? baseline.State.Position.Z : current.Position.Z),
            new CharacterVector3(
                restoreRotationX
                    ? CharacterAngleMath.NearestPathFromCurrent(
                        baseline.State.LocalEulerAngles.X,
                        current.LocalEulerAngles.X)
                    : current.LocalEulerAngles.X,
                restoreRotationY
                    ? CharacterAngleMath.NearestPathFromCurrent(
                        baseline.State.LocalEulerAngles.Y,
                        current.LocalEulerAngles.Y)
                    : current.LocalEulerAngles.Y,
                restoreRotationZ
                    ? CharacterAngleMath.NearestPathFromCurrent(
                        baseline.State.LocalEulerAngles.Z,
                        current.LocalEulerAngles.Z)
                    : current.LocalEulerAngles.Z));

        return Result<CharacterTransformTarget>.Ok(new CharacterTransformTarget(
            restored,
            restoreX || restoreY || resetAll,
            restoreRotationX || restoreRotationY || restoreRotationZ || resetAll,
            0,
            CharacterTransformEasing.Linear,
            false));
    }

    private static CharacterTransformState PlanSet(
        CharacterTransformCommand command,
        CharacterTransformState current,
        CharacterTransformBaseline baseline)
    {
        var position = new CharacterVector3(
            command.X ?? current.Position.X,
            command.Y ?? current.Position.Y,
            current.Position.Z);
        var rotation = new CharacterVector3(
            PlanSetRotation(current.LocalEulerAngles.X, command.RotationXDegrees),
            command.FlipX.HasValue
                ? FlipY(baseline.State.LocalEulerAngles.Y, current.LocalEulerAngles.Y, command.FlipX.Value)
                : current.LocalEulerAngles.Y,
            PlanSetRotation(current.LocalEulerAngles.Z, command.RotationDegrees));
        return new CharacterTransformState(position, rotation);
    }

    private static CharacterTransformState PlanMove(
        CharacterTransformCommand command,
        CharacterTransformState current)
    {
        var position = new CharacterVector3(
            current.Position.X + (command.DeltaX ?? 0f),
            current.Position.Y + (command.DeltaY ?? 0f),
            current.Position.Z);
        var rotation = new CharacterVector3(
            PlanMoveRotation(current.LocalEulerAngles.X, command.DeltaRotationXDegrees),
            current.LocalEulerAngles.Y,
            PlanMoveRotation(current.LocalEulerAngles.Z, command.DeltaRotationDegrees));
        return new CharacterTransformState(position, rotation);
    }

    private static float PlanSetRotation(float current, float? absoluteRotation) => !absoluteRotation.HasValue
        ? current
        : CharacterAngleMath.IsDeliberateMultiTurn(absoluteRotation.Value)
            ? absoluteRotation.Value
            : CharacterAngleMath.NearestPathFromCurrent(absoluteRotation.Value, current);

    private static float PlanMoveRotation(
        float current,
        float? deltaRotation)
    {
        if (!deltaRotation.HasValue)
        {
            return current;
        }

        float unclamped = current + deltaRotation.Value;
        return CharacterAngleMath.IsDeliberateMultiTurn(deltaRotation.Value)
            ? unclamped
            : CharacterAngleMath.NearestPathFromCurrent(
                unclamped,
                current);
    }

    private static float FlipY(float baselineY, float currentY, bool flipped) =>
        CharacterAngleMath.NearestPathFromCurrent(
            CharacterAngleMath.Normalize360(baselineY)
                + (flipped ? CharacterAngleMath.HalfTurnDegrees : 0f),
            currentY);

    private static CharacterTransformState AdjustToNearestEquivalent(
        CharacterTransformState target,
        CharacterTransformState current) =>
        new(
            target.Position,
            new CharacterVector3(
                CharacterAngleMath.NearestPathFromCurrent(
                    target.LocalEulerAngles.X,
                    current.LocalEulerAngles.X),
                CharacterAngleMath.NearestPathFromCurrent(
                    target.LocalEulerAngles.Y,
                    current.LocalEulerAngles.Y),
                CharacterAngleMath.NearestPathFromCurrent(
                    target.LocalEulerAngles.Z,
                    current.LocalEulerAngles.Z)));

    private static bool BaselineMatches(
        CharacterTransformCommand command,
        CharacterTransformBaseline? baseline,
        out string error)
    {
        if (baseline == null)
        {
            error = "Character transform baseline is missing.";
            return false;
        }

        if (baseline.PublicSlot != command.PublicSlot)
        {
            error = "Character transform baseline belongs to a different slot.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(baseline.SceneIdentity)
            || string.IsNullOrWhiteSpace(baseline.OccupantIdentifier))
        {
            error = "Character transform baseline identity is invalid.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
