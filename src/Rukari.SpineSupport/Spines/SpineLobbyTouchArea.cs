namespace Rukari.SpineSupport.Spines;

public readonly record struct SpineLobbyTouchRect(int X, int Y, int Width, int Height);

/// <summary>
/// Turns a lobby's own touch marker (the <c>Touch_Point</c> / <c>Touch_Eye</c> bones and slots the lobby spines
/// carry) into the rectangle the official <c>#touch</c> wait expects, so the author does not have to type
/// coordinates that only fit one animation frame.
///
/// <para>
/// Pure arithmetic on purpose: the caller supplies the marker's screen position and the authored size, and this
/// decides whether a rectangle can be built at all and keeps it inside the screen. A marker that is off screen
/// yields no rectangle — the authored one is then kept, which is the safe direction for a wait the player is
/// sitting on.
/// </para>
/// </summary>
public static class SpineLobbyTouchArea
{
    /// <summary>Used when the authored <c>#touch</c> rectangle carries no usable width or height.</summary>
    public const int DefaultSize = 160;

    /// <summary>
    /// A screen rectangle centred on the marker, clamped into the screen. Null when there is no usable screen or
    /// the marker is not on it.
    /// </summary>
    public static SpineLobbyTouchRect? Around(
        int markerX,
        int markerY,
        int authoredWidth,
        int authoredHeight,
        int screenWidth,
        int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return null;
        if (markerX < 0 || markerY < 0 || markerX > screenWidth || markerY > screenHeight) return null;

        int width = authoredWidth > 0 ? authoredWidth : DefaultSize;
        int height = authoredHeight > 0 ? authoredHeight : DefaultSize;
        width = Math.Min(width, screenWidth);
        height = Math.Min(height, screenHeight);
        int x = Math.Clamp(markerX - (width / 2), 0, screenWidth - width);
        int y = Math.Clamp(markerY - (height / 2), 0, screenHeight - height);
        return new SpineLobbyTouchRect(x, y, width, height);
    }
}
