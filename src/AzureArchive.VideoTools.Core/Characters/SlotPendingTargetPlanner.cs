using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

/// <summary>
/// Pure target computation for a consumed slot pending transform. The current
/// state is the freshly entered occupant's official state; absolute axes come
/// from the command, omitted axes keep the official entry value, relative
/// axes add to it, and an explicit flip adds 180 degrees to the official
/// facing exactly like the first transform of any other session.
/// </summary>
public static class SlotPendingTargetPlanner
{
    public static Result<CharacterTransformTarget> Compute(
        CharacterTransformCommand command,
        CharacterTransformState current)
    {
        Result validation = CharacterTransformCommandValidator.Validate(command);
        if (!validation.Success)
        {
            return Result<CharacterTransformTarget>.Fail(validation.Error);
        }

        if (!current.Position.IsFinite || !current.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformTarget>.Fail(
                "Current character transform is invalid.");
        }

        if (command.Operation == CharacterTransformOperation.Reset)
        {
            return Result<CharacterTransformTarget>.Fail(
                "Slot pending transforms do not support reset.");
        }

        CharacterTransformState target;
        bool positionChanged;
        bool rotationChanged;
        switch (command.Operation)
        {
            case CharacterTransformOperation.Set:
                target = ComputeSet(command, current);
                positionChanged = command.X.HasValue || command.Y.HasValue;
                rotationChanged = command.RotationDegrees.HasValue || command.FlipX.HasValue;
                break;
            case CharacterTransformOperation.Move:
                target = ComputeMove(command, current);
                positionChanged = command.DeltaX.HasValue || command.DeltaY.HasValue;
                rotationChanged = command.DeltaRotationDegrees.HasValue;
                break;
            default:
                return Result<CharacterTransformTarget>.Fail(
                    "Unknown slot pending operation.");
        }

        if (!target.Position.IsFinite || !target.LocalEulerAngles.IsFinite)
        {
            return Result<CharacterTransformTarget>.Fail(
                "Computed slot pending target is not finite.");
        }

        return Result<CharacterTransformTarget>.Ok(new CharacterTransformTarget(
            target,
            positionChanged,
            rotationChanged,
            0,
            CharacterTransformEasing.Linear,
            IsReset: false));
    }

    private static CharacterTransformState ComputeSet(
        CharacterTransformCommand command,
        CharacterTransformState current)
    {
        var position = new CharacterVector3(
            command.X ?? current.Position.X,
            command.Y ?? current.Position.Y,
            current.Position.Z);
        var rotation = new CharacterVector3(
            current.LocalEulerAngles.X,
            command.FlipX.HasValue
                ? CharacterAngleMath.NearestEquivalent(
                    current.LocalEulerAngles.Y
                        + (command.FlipX.Value ? CharacterAngleMath.HalfTurnDegrees : 0f),
                    current.LocalEulerAngles.Y)
                : current.LocalEulerAngles.Y,
            command.RotationDegrees.HasValue
                ? CharacterAngleMath.NearestEquivalent(
                    command.RotationDegrees.Value,
                    current.LocalEulerAngles.Z)
                : current.LocalEulerAngles.Z);
        return new CharacterTransformState(position, rotation);
    }

    private static CharacterTransformState ComputeMove(
        CharacterTransformCommand command,
        CharacterTransformState current)
    {
        var position = new CharacterVector3(
            current.Position.X + (command.DeltaX ?? 0f),
            current.Position.Y + (command.DeltaY ?? 0f),
            current.Position.Z);
        var rotation = new CharacterVector3(
            current.LocalEulerAngles.X,
            current.LocalEulerAngles.Y,
            CharacterAngleMath.NearestEquivalent(
                current.LocalEulerAngles.Z + (command.DeltaRotationDegrees ?? 0f),
                current.LocalEulerAngles.Z));
        return new CharacterTransformState(position, rotation);
    }
}
