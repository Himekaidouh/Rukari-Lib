using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Cameras;

/// <summary>Composes the outer overall camera with the inner background camera.</summary>
public static class SceneCameraCompositionPlanner
{
    public static SceneCameraPhysicalWrites WritesFor(SceneCameraMotionFields fields) => new(
        fields != SceneCameraMotionFields.None,
        (fields & (SceneCameraMotionFields.OverallZoom | SceneCameraMotionFields.BackgroundZoom)) != 0,
        (fields & (SceneCameraMotionFields.OverallPosition | SceneCameraMotionFields.OverallZoom)) != 0,
        (fields & SceneCameraMotionFields.OverallZoom) != 0);

    public static Result<SceneCameraComposedLayers> Compose(SceneCameraComposition composition)
    {
        Result validation = SceneCameraCommandValidator.ValidateComposition(composition);
        if (!validation.Success) return Result<SceneCameraComposedLayers>.Fail(validation.Error);

        SceneCameraState overall = composition.Overall;
        SceneCameraState background = composition.Background;
        float zoom = overall.Zoom * background.Zoom;
        var spine = new SceneCameraLayerComposition(
            overall.X * overall.Zoom, overall.Y * overall.Zoom, overall.Zoom);
        var back = new SceneCameraLayerComposition(
            spine.OffsetX + background.X * zoom,
            spine.OffsetY + background.Y * zoom,
            zoom);
        return Result<SceneCameraComposedLayers>.Ok(new(back, spine));
    }
}
