namespace Rukari.SpineSupport.Spines;

public sealed record SpineLobbyThumbnailLayout(
    float ScaleX,
    float ScaleY,
    float PositionX,
    float PositionY);

/// <summary>
/// Fits skeleton-local bounds into a card centered at the origin. Card dimensions,
/// padding, and the returned position use the transform parent's coordinate space.
/// Padding is reserved on each of the card's sides.
/// </summary>
public static class SpineLobbyThumbnailFit
{
    public static SpineLobbyThumbnailLayout? Calculate(
        float boundsX,
        float boundsY,
        float boundsWidth,
        float boundsHeight,
        float cardWidth,
        float cardHeight,
        float currentScaleX,
        float currentScaleY,
        float padding = 16f)
    {
        if (!float.IsFinite(boundsX) || !float.IsFinite(boundsY)
            || !float.IsFinite(boundsWidth) || !float.IsFinite(boundsHeight)
            || !float.IsFinite(cardWidth) || !float.IsFinite(cardHeight)
            || !float.IsFinite(currentScaleX) || !float.IsFinite(currentScaleY)
            || !float.IsFinite(padding)
            || boundsWidth <= 0f || boundsHeight <= 0f
            || currentScaleX == 0f || currentScaleY == 0f || padding < 0f)
        {
            return null;
        }

        double availableWidth = (double)cardWidth - 2d * padding;
        double availableHeight = (double)cardHeight - 2d * padding;
        if (availableWidth <= 0d || availableHeight <= 0d)
        {
            return null;
        }

        double shrink = Math.Min(1d, Math.Min(
            availableWidth / (boundsWidth * Math.Abs((double)currentScaleX)),
            availableHeight / (boundsHeight * Math.Abs((double)currentScaleY))));
        float scaleX = (float)(currentScaleX * shrink);
        float scaleY = (float)(currentScaleY * shrink);
        float positionX = (float)(-((double)boundsX + boundsWidth / 2d) * scaleX);
        float positionY = (float)(-((double)boundsY + boundsHeight / 2d) * scaleY);
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY)
            || !float.IsFinite(positionX) || !float.IsFinite(positionY)
            || scaleX == 0f || scaleY == 0f)
        {
            return null;
        }

        return new SpineLobbyThumbnailLayout(scaleX, scaleY, positionX, positionY);
    }
}
