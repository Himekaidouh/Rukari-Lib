using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

public sealed class SceneCameraPlanner : ISceneCameraPlanner
{
    public Result<SceneCameraTarget> Plan(
        SceneCameraCommand command,
        SceneCameraState current,
        SceneCameraBaseline baseline)
    {
        Result validation = ValidateInputs(command, current, baseline);
        if (!validation.Success)
        {
            return Result<SceneCameraTarget>.Fail(validation.Error);
        }

        SceneCameraState target = command.Operation switch
        {
            SceneCameraOperation.Set => new(
                command.X ?? current.X,
                command.Y ?? current.Y,
                command.Zoom ?? current.Zoom),
            SceneCameraOperation.Move => new(
                current.X + (command.DeltaX ?? 0f),
                current.Y + (command.DeltaY ?? 0f),
                current.Zoom + (command.DeltaZoom ?? 0f)),
            SceneCameraOperation.Reset => SceneCameraState.Default,
            _ => current
        };

        Result targetValidation = SceneCameraCommandValidator.ValidateTarget(target);
        if (!targetValidation.Success)
        {
            return Result<SceneCameraTarget>.Fail(targetValidation.Error);
        }

        bool reset = command.Operation == SceneCameraOperation.Reset;
        return Result<SceneCameraTarget>.Ok(new(
            target,
            reset || command.X.HasValue || command.Y.HasValue
                || command.DeltaX.HasValue || command.DeltaY.HasValue,
            reset || command.Zoom.HasValue || command.DeltaZoom.HasValue,
            command.DurationMilliseconds,
            command.Easing,
            reset));
    }

    public Result<SceneCameraTarget> PlanReplayRestore(
        SceneCameraCommand command,
        SceneCameraState current,
        SceneCameraBaseline baseline)
    {
        Result validation = ValidateInputs(command, current, baseline);
        if (!validation.Success)
        {
            return Result<SceneCameraTarget>.Fail(validation.Error);
        }

        bool reset = command.Operation == SceneCameraOperation.Reset;
        bool restoreX = reset || command.X.HasValue || command.DeltaX.HasValue;
        bool restoreY = reset || command.Y.HasValue || command.DeltaY.HasValue;
        bool restoreZoom = reset || command.Zoom.HasValue || command.DeltaZoom.HasValue;
        var restored = new SceneCameraState(
            restoreX ? baseline.State.X : current.X,
            restoreY ? baseline.State.Y : current.Y,
            restoreZoom ? baseline.State.Zoom : current.Zoom);

        return Result<SceneCameraTarget>.Ok(new(
            restored,
            restoreX || restoreY,
            restoreZoom,
            0,
            command.Easing,
            IsReset: false));
    }

    private static Result ValidateInputs(
        SceneCameraCommand command,
        SceneCameraState current,
        SceneCameraBaseline baseline)
    {
        Result commandValidation = SceneCameraCommandValidator.Validate(command);
        if (!commandValidation.Success)
        {
            return commandValidation;
        }

        Result currentValidation = SceneCameraCommandValidator.ValidateTarget(current);
        if (!currentValidation.Success)
        {
            return Result.Fail($"Current scene camera state is invalid: {currentValidation.Error}");
        }

        if (string.IsNullOrWhiteSpace(baseline.SceneIdentity))
        {
            return Result.Fail("Scene camera baseline identity is empty.");
        }

        Result baselineValidation = SceneCameraCommandValidator.ValidateTarget(baseline.State);
        return baselineValidation.Success
            ? Result.Ok()
            : Result.Fail($"Scene camera baseline is invalid: {baselineValidation.Error}");
    }
}
