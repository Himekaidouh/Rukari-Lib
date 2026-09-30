using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.VisualEditor;

public readonly record struct VisualEditorPoint(float X, float Y)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y);
}

public readonly record struct VisualEditorRect(float X, float Y, float Width, float Height)
{
    public bool IsValid =>
        float.IsFinite(X)
        && float.IsFinite(Y)
        && float.IsFinite(Width)
        && float.IsFinite(Height)
        && Width > 0f
        && Height > 0f;
}

public static class VisualEditorCanvasMath
{
    public const float StoryWidth = 2960f;
    public const float StoryHalfWidth = StoryWidth / 2f;

    /// <summary>
    /// The viewport shows more world than the game screen: characters dragged
    /// slightly off-screen stay visible and clickable inside the frame. 1.3
    /// means the frame spans 130% of the story width (and height), shrinking
    /// on-stage content to about 77% scale.
    /// </summary>
    public const float ViewportZoomOutFactor = 1.3f;
    public const float SlotAnchorSpacing = 520f;
    public const int MinimumPublicSlot = 1;
    public const int MaximumPublicSlot = 5;

    public static Result<VisualEditorPoint> SlotAnchor(int publicSlot)
    {
        if (publicSlot < MinimumPublicSlot || publicSlot > MaximumPublicSlot)
        {
            return Result<VisualEditorPoint>.Fail(
                "Physical character slot must be between 1 and 5.");
        }

        return Result<VisualEditorPoint>.Ok(new VisualEditorPoint(
            (publicSlot - 3) * SlotAnchorSpacing,
            0f));
    }

    public static Result<VisualEditorPoint> SlotLocalToStory(
        int publicSlot,
        VisualEditorPoint localOffset)
    {
        if (!localOffset.IsFinite)
        {
            return Result<VisualEditorPoint>.Fail("Slot-local offset must be finite.");
        }

        Result<VisualEditorPoint> anchor = SlotAnchor(publicSlot);
        return !anchor.Success
            ? Result<VisualEditorPoint>.Fail(anchor.Error)
            : Result<VisualEditorPoint>.Ok(new VisualEditorPoint(
                anchor.Value.X + localOffset.X,
                anchor.Value.Y + localOffset.Y));
    }

    public static Result<VisualEditorPoint> StoryToSlotLocal(
        int publicSlot,
        VisualEditorPoint storyPosition)
    {
        if (!storyPosition.IsFinite)
        {
            return Result<VisualEditorPoint>.Fail("Story position must be finite.");
        }

        Result<VisualEditorPoint> anchor = SlotAnchor(publicSlot);
        return !anchor.Success
            ? Result<VisualEditorPoint>.Fail(anchor.Error)
            : Result<VisualEditorPoint>.Ok(new VisualEditorPoint(
                storyPosition.X - anchor.Value.X,
                storyPosition.Y - anchor.Value.Y));
    }

    public static Result<float> LogicalStoryHeight(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return Result<float>.Fail("Screen dimensions must be positive.");
        }

        return Result<float>.Ok(StoryWidth * pixelHeight / pixelWidth);
    }

    public static Result<VisualEditorPoint> StoryToViewport(
        VisualEditorPoint story,
        float storyHeight,
        VisualEditorRect viewport)
    {
        Result validation = Validate(story, storyHeight, viewport);
        if (!validation.Success)
        {
            return Result<VisualEditorPoint>.Fail(validation.Error);
        }

        float extentWidth = StoryWidth * ViewportZoomOutFactor;
        float halfExtentWidth = StoryHalfWidth * ViewportZoomOutFactor;
        float extentHeight = storyHeight * ViewportZoomOutFactor;
        float x = viewport.X
            + ((story.X + halfExtentWidth) / extentWidth * viewport.Width);
        float y = viewport.Y
            + ((story.Y + (extentHeight / 2f)) / extentHeight * viewport.Height);
        return Result<VisualEditorPoint>.Ok(new VisualEditorPoint(x, y));
    }

    public static Result<VisualEditorPoint> ViewportToStory(
        VisualEditorPoint viewportPoint,
        float storyHeight,
        VisualEditorRect viewport)
    {
        Result validation = Validate(viewportPoint, storyHeight, viewport);
        if (!validation.Success)
        {
            return Result<VisualEditorPoint>.Fail(validation.Error);
        }

        float extentWidth = StoryWidth * ViewportZoomOutFactor;
        float halfExtentWidth = StoryHalfWidth * ViewportZoomOutFactor;
        float extentHeight = storyHeight * ViewportZoomOutFactor;
        float x = ((viewportPoint.X - viewport.X) / viewport.Width * extentWidth)
            - halfExtentWidth;
        float y = ((viewportPoint.Y - viewport.Y) / viewport.Height * extentHeight)
            - (extentHeight / 2f);
        return Result<VisualEditorPoint>.Ok(new VisualEditorPoint(x, y));
    }

    public static bool ContainsRotatedRect(
        VisualEditorPoint point,
        VisualEditorPoint center,
        float width,
        float height,
        float rotationDegrees)
    {
        if (!point.IsFinite
            || !center.IsFinite
            || !float.IsFinite(width)
            || !float.IsFinite(height)
            || !float.IsFinite(rotationDegrees)
            || width <= 0f
            || height <= 0f)
        {
            return false;
        }

        float radians = -rotationDegrees * (MathF.PI / 180f);
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        float deltaX = point.X - center.X;
        float deltaY = point.Y - center.Y;
        float localX = (deltaX * cosine) - (deltaY * sine);
        float localY = (deltaX * sine) + (deltaY * cosine);
        const float epsilon = 0.001f;
        return MathF.Abs(localX) <= (width / 2f) + epsilon
            && MathF.Abs(localY) <= (height / 2f) + epsilon;
    }

    public static bool ExceedsDragThreshold(
        VisualEditorPoint pressPoint,
        VisualEditorPoint currentPoint,
        float threshold)
    {
        if (!pressPoint.IsFinite
            || !currentPoint.IsFinite
            || !float.IsFinite(threshold)
            || threshold < 0f)
        {
            return false;
        }

        float deltaX = currentPoint.X - pressPoint.X;
        float deltaY = currentPoint.Y - pressPoint.Y;
        return (deltaX * deltaX) + (deltaY * deltaY) >= threshold * threshold;
    }

    private static Result Validate(
        VisualEditorPoint point,
        float storyHeight,
        VisualEditorRect viewport)
    {
        if (!point.IsFinite)
        {
            return Result.Fail("Visual editor point must be finite.");
        }

        if (!float.IsFinite(storyHeight) || storyHeight <= 0f)
        {
            return Result.Fail("Story height must be positive and finite.");
        }

        return viewport.IsValid
            ? Result.Ok()
            : Result.Fail("Viewport must have positive finite dimensions.");
    }
}
