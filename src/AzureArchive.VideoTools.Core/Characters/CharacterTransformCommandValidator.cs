using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Characters;

public static class CharacterTransformCommandValidator
{
    public const int MinimumPublicSlot = 1;
    public const int MaximumPublicSlot = 5;
    public const int MaximumDurationMilliseconds = 600_000;
    public const int MaximumRotationDegrees = 3600;

    public static Result Validate(CharacterTransformCommand? command)
    {
        if (command == null)
        {
            return Result.Fail("Character transform command is null.");
        }

        if (command.PublicSlot < MinimumPublicSlot || command.PublicSlot > MaximumPublicSlot)
        {
            return Result.Fail("Physical character slot must be between 1 and 5.");
        }

        if (command.DurationMilliseconds < 0
            || command.DurationMilliseconds > MaximumDurationMilliseconds)
        {
            return Result.Fail(
                $"Duration must be between 0 and {MaximumDurationMilliseconds} milliseconds.");
        }

        if (!AllFinite(
                command.X,
                command.Y,
                command.DeltaX,
                command.DeltaY,
                command.RotationDegrees,
                command.DeltaRotationDegrees,
                command.RotationXDegrees,
                command.DeltaRotationXDegrees))
        {
            return Result.Fail("Character transform values must be finite numbers.");
        }

        if (Math.Abs(command.RotationDegrees ?? 0f) > MaximumRotationDegrees
            || Math.Abs(command.DeltaRotationDegrees ?? 0f) > MaximumRotationDegrees
            || Math.Abs(command.RotationXDegrees ?? 0f) > MaximumRotationDegrees
            || Math.Abs(command.DeltaRotationXDegrees ?? 0f) > MaximumRotationDegrees)
        {
            return Result.Fail(
                $"Rotation must stay within ±{MaximumRotationDegrees} degrees "
                + "(ten full turns); chain repeated commands for longer spins.");
        }

        return command.Operation switch
        {
            CharacterTransformOperation.Set => ValidateSet(command),
            CharacterTransformOperation.Move => ValidateMove(command),
            CharacterTransformOperation.Reset => ValidateReset(command),
            _ => Result.Fail("Unknown character transform operation.")
        };
    }

    private static Result ValidateSet(CharacterTransformCommand command)
    {
        if (command.DeltaX.HasValue
            || command.DeltaY.HasValue
            || command.DeltaRotationDegrees.HasValue
            || command.DeltaRotationXDegrees.HasValue)
        {
            return Result.Fail("Set commands cannot contain relative delta properties.");
        }

        if (!command.X.HasValue
            && !command.Y.HasValue
            && !command.RotationDegrees.HasValue
            && !command.RotationXDegrees.HasValue
            && !command.FlipX.HasValue)
        {
            return Result.Fail("Set commands must change at least one property.");
        }

        return Result.Ok();
    }

    private static Result ValidateMove(CharacterTransformCommand command)
    {
        if (command.X.HasValue
            || command.Y.HasValue
            || command.RotationDegrees.HasValue
            || command.RotationXDegrees.HasValue
            || command.FlipX.HasValue)
        {
            return Result.Fail("Move commands cannot contain absolute or flip properties.");
        }

        if (!command.DeltaX.HasValue
            && !command.DeltaY.HasValue
            && !command.DeltaRotationDegrees.HasValue
            && !command.DeltaRotationXDegrees.HasValue)
        {
            return Result.Fail("Move commands must change at least one property.");
        }

        return Result.Ok();
    }

    private static Result ValidateReset(CharacterTransformCommand command)
    {
        if (command.X.HasValue
            || command.Y.HasValue
            || command.DeltaX.HasValue
            || command.DeltaY.HasValue
            || command.RotationDegrees.HasValue
            || command.DeltaRotationDegrees.HasValue
            || command.RotationXDegrees.HasValue
            || command.DeltaRotationXDegrees.HasValue
            || command.FlipX.HasValue)
        {
            return Result.Fail("Reset commands cannot contain transform properties.");
        }

        return Result.Ok();
    }

    private static bool AllFinite(params float?[] values) =>
        values.All(value => !value.HasValue || float.IsFinite(value.Value));
}
