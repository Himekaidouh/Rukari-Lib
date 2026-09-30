using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

public static class SceneCameraCommandValidator
{
    public const float MinimumZoom = 0.1f;
    public const float MaximumZoom = 8f;
    public const int MaximumDurationMilliseconds = 600_000;

    public static Result Validate(SceneCameraCommand? command)
    {
        if (command == null)
        {
            return Result.Fail("Scene camera command is null.");
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
                command.Zoom,
                command.DeltaZoom))
        {
            return Result.Fail("Scene camera values must be finite numbers.");
        }

        return command.Operation switch
        {
            SceneCameraOperation.Set => ValidateSet(command),
            SceneCameraOperation.Move => ValidateMove(command),
            SceneCameraOperation.Reset => ValidateReset(command),
            _ => Result.Fail("Unknown scene camera operation.")
        };
    }

    public static Result ValidateTarget(SceneCameraState state)
    {
        if (!state.IsFinite)
        {
            return Result.Fail("Scene camera target must contain finite values.");
        }

        return state.Zoom is >= MinimumZoom and <= MaximumZoom
            ? Result.Ok()
            : Result.Fail($"Zoom must be between {MinimumZoom} and {MaximumZoom}.");
    }

    private static Result ValidateSet(SceneCameraCommand command)
    {
        if (command.DeltaX.HasValue || command.DeltaY.HasValue || command.DeltaZoom.HasValue)
        {
            return Result.Fail("Camera set commands cannot contain relative delta properties.");
        }

        if (!command.X.HasValue && !command.Y.HasValue && !command.Zoom.HasValue)
        {
            return Result.Fail("Camera set commands must change at least one property.");
        }

        if (command.Zoom.HasValue
            && command.Zoom.Value is < MinimumZoom or > MaximumZoom)
        {
            return Result.Fail($"Zoom must be between {MinimumZoom} and {MaximumZoom}.");
        }

        return Result.Ok();
    }

    private static Result ValidateMove(SceneCameraCommand command)
    {
        if (command.X.HasValue || command.Y.HasValue || command.Zoom.HasValue)
        {
            return Result.Fail("Camera move commands cannot contain absolute properties.");
        }

        return command.DeltaX.HasValue || command.DeltaY.HasValue || command.DeltaZoom.HasValue
            ? Result.Ok()
            : Result.Fail("Camera move commands must change at least one property.");
    }

    private static Result ValidateReset(SceneCameraCommand command)
    {
        if (command.X.HasValue
            || command.Y.HasValue
            || command.DeltaX.HasValue
            || command.DeltaY.HasValue
            || command.Zoom.HasValue
            || command.DeltaZoom.HasValue)
        {
            return Result.Fail("Camera reset commands cannot contain transform properties.");
        }

        return Result.Ok();
    }

    private static bool AllFinite(params float?[] values) =>
        values.All(value => !value.HasValue || float.IsFinite(value.Value));
}
