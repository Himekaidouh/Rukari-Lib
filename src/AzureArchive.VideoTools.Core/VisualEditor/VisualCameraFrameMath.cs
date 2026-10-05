using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.VisualEditor;

public static class VisualCameraFrameMath
{
    public static Result<VisualEditorRect> CameraToViewportFrame(
        SceneCameraComposition composition,
        SceneCameraScope scope,
        float storyHeight,
        VisualEditorRect viewport)
    {
        Result<SceneCameraComposedLayers> composed = SceneCameraCompositionPlanner.Compose(composition);
        if (!composed.Success || composed.Value == null)
            return Result<VisualEditorRect>.Fail(composed.Error);
        if (!Enum.IsDefined(typeof(SceneCameraScope), scope))
            return Result<VisualEditorRect>.Fail("Unknown camera scope.");
        SceneCameraLayerComposition layer = scope == SceneCameraScope.Overall
            ? composed.Value.Spine : composed.Value.Back;
        return CameraToViewportFrame(new SceneCameraState(
            layer.OffsetX / layer.Zoom, layer.OffsetY / layer.Zoom, layer.Zoom), storyHeight, viewport);
    }

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
        SceneCameraComposition composition,
        SceneCameraScope scope,
        float storyHeight,
        VisualEditorRect viewport)
    {
        Result validation = SceneCameraCommandValidator.ValidateComposition(composition);
        if (!validation.Success) return Result<SceneCameraState>.Fail(validation.Error);
        if (!Enum.IsDefined(typeof(SceneCameraScope), scope))
            return Result<SceneCameraState>.Fail("Unknown camera scope.");
        Result<VisualEditorPoint> story = VisualEditorCanvasMath.ViewportToStory(viewportCenter, storyHeight, viewport);
        if (!story.Success) return Result<SceneCameraState>.Fail(story.Error);
        SceneCameraState current = composition.ForScope(scope);
        SceneCameraState moved = scope == SceneCameraScope.Overall
            ? current with { X = story.Value.X, Y = story.Value.Y }
            : current with
            {
                X = story.Value.X - composition.Overall.X / current.Zoom,
                Y = story.Value.Y - composition.Overall.Y / current.Zoom
            };
        validation = SceneCameraCommandValidator.ValidateComposition(composition.WithScope(scope, moved));
        return validation.Success ? Result<SceneCameraState>.Ok(moved)
            : Result<SceneCameraState>.Fail(validation.Error);
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
        SceneCameraComposition composition,
        SceneCameraScope scope,
        VisualEditorRect viewport)
    {
        Result validation = SceneCameraCommandValidator.ValidateComposition(composition);
        if (!validation.Success) return Result<float>.Fail(validation.Error);
        if (!Enum.IsDefined(typeof(SceneCameraScope), scope))
            return Result<float>.Fail("Unknown camera scope.");
        Result<float> effective = ZoomFromFrameWidth(frameWidth, viewport);
        if (!effective.Success) return effective;
        float zoom = scope == SceneCameraScope.Overall ? effective.Value : effective.Value / composition.Overall.Zoom;
        validation = SceneCameraCommandValidator.ValidateComposition(composition.WithScope(scope,
            composition.ForScope(scope) with { Zoom = zoom }));
        return validation.Success ? Result<float>.Ok(zoom) : Result<float>.Fail(validation.Error);
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
