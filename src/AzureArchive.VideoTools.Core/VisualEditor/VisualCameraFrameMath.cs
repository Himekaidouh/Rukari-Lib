using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.VisualEditor;

public static class VisualCameraFrameMath
{
    public static Result<VisualEditorRect> CameraToViewportFrame(
        SceneCameraState camera,
        float storyHeight,
        VisualEditorRect viewport)
    {
        Result cameraValidation = SceneCameraCommandValidator.ValidateTarget(camera);
        if (!cameraValidation.Success)
        {
            return Result<VisualEditorRect>.Fail(cameraValidation.Error);
        }

        Result<VisualEditorPoint> mapped = VisualEditorCanvasMath.StoryToViewport(
            new VisualEditorPoint(camera.X, camera.Y),
            storyHeight,
            viewport);
        if (!mapped.Success)
        {
            return Result<VisualEditorRect>.Fail(mapped.Error);
        }

        float width = FrameWidth(viewport, camera.Zoom);
        float height = FrameHeight(viewport, camera.Zoom);
        return Result<VisualEditorRect>.Ok(new VisualEditorRect(
            mapped.Value.X - (width / 2f),
            mapped.Value.Y - (height / 2f),
            width,
            height));
    }

    public static Result<SceneCameraState> MoveFrameCenter(
        VisualEditorPoint viewportCenter,
        SceneCameraState current,
        float storyHeight,
        VisualEditorRect viewport)
    {
        Result cameraValidation = SceneCameraCommandValidator.ValidateTarget(current);
        if (!cameraValidation.Success)
        {
            return Result<SceneCameraState>.Fail(cameraValidation.Error);
        }

        Result<VisualEditorPoint> story = VisualEditorCanvasMath.ViewportToStory(
            viewportCenter,
            storyHeight,
            viewport);
        return !story.Success
            ? Result<SceneCameraState>.Fail(story.Error)
            : Result<SceneCameraState>.Ok(new SceneCameraState(
                story.Value.X,
                story.Value.Y,
                current.Zoom));
    }

    public static Result<float> ZoomFromFrameWidth(
        float frameWidth,
        VisualEditorRect viewport)
    {
        if (!float.IsFinite(frameWidth) || frameWidth <= 0f || !viewport.IsValid)
        {
            return Result<float>.Fail(
                "Camera frame width and viewport must be positive finite values.");
        }

        float zoom = viewport.Width
            / (VisualEditorCanvasMath.ViewportZoomOutFactor * frameWidth);
        return zoom is >= SceneCameraCommandValidator.MinimumZoom
            and <= SceneCameraCommandValidator.MaximumZoom
                ? Result<float>.Ok(zoom)
                : Result<float>.Fail(
                    $"Zoom must be between {SceneCameraCommandValidator.MinimumZoom} "
                    + $"and {SceneCameraCommandValidator.MaximumZoom}.");
    }

    public static float FrameWidth(VisualEditorRect viewport, float zoom) =>
        viewport.Width / (VisualEditorCanvasMath.ViewportZoomOutFactor * zoom);

    public static float FrameHeight(VisualEditorRect viewport, float zoom) =>
        viewport.Height / (VisualEditorCanvasMath.ViewportZoomOutFactor * zoom);
}
