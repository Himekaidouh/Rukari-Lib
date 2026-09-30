namespace Rukari.Lib.Tools;

/// <summary>
/// The module card's scroll arithmetic. It is kept out of the renderer so the rules a wheel notch and a thumb drag
/// both obey can be tested without a canvas, and so the layout and the drawing cannot disagree about where the
/// thumb is.
///
/// The card scrolls in whole rows, never in pixels. That is what keeps the number of controls on screen constant:
/// a row is either shown or not, so a list of forty mods costs the same per frame as a list of four.
/// </summary>
public static class ToolScroll
{
    /// <summary>
    /// The nearest row a scroll position may show. Scrolling past the end is clamped instead of rejected, so a
    /// caller that keeps its position across a window resize — where fewer rows fit — lands on the last full page
    /// rather than on a blank card.
    /// </summary>
    public static int ClampFirst(int first, int total, int visible)
    {
        int rows = Math.Max(0, visible);
        int items = Math.Max(0, total);
        return Math.Clamp(first, 0, Math.Max(0, items - rows));
    }

    /// <summary>Whether a list needs a scrollbar: it has more rows than fit, and at least one row does fit.</summary>
    public static bool Needed(int total, int visible) => visible > 0 && total > visible;

    /// <summary>
    /// Rows one wheel notch moves. A notch moves exactly one row so the list tracks the wheel instead of jumping a
    /// page; a positive delta is the wheel pushed away from the user, which scrolls the list up.
    /// </summary>
    public static int WheelRows(float scrollDelta) =>
        scrollDelta > 0f ? -1 : scrollDelta < 0f ? 1 : 0;

    /// <summary>
    /// The thumb of a scrollbar, as a rectangle in the track's own coordinates. Its length is the visible fraction
    /// of the list, never shorter than <paramref name="minimumLength"/> and never longer than the track, and its
    /// position along the track is the scroll position over the rows that can still be scrolled. Y grows upward,
    /// like every other rectangle in the drawer.
    /// </summary>
    public static ToolInputRect Thumb(ToolInputRect track, int first, int total, int visible, float minimumLength)
    {
        if (!Needed(total, visible) || !track.IsValid) return default;
        float length = Math.Max(Math.Min(Math.Max(1f, minimumLength), track.Height),
            track.Height * Math.Clamp(visible, 0, total) / Math.Max(1, total));
        float travel = Math.Max(0f, track.Height - length);
        int scrollable = Math.Max(1, total - visible);
        float offset = travel * ClampFirst(first, total, visible) / scrollable;
        return new ToolInputRect(track.X, track.Y + offset, track.Width, length);
    }

    /// <summary>
    /// The first row a thumb whose top edge sits at <paramref name="pointerY"/> asks for. It is the inverse of
    /// <see cref="Thumb"/>: dragging a thumb to the top of the track shows the first row and dragging it to the
    /// bottom shows the last one, so the two directions of the same gesture cannot disagree.
    /// </summary>
    public static int RowForThumbTop(ToolInputRect track, float thumbLength, float pointerY, int total, int visible)
    {
        if (!Needed(total, visible) || !track.IsValid) return 0;
        float length = Math.Clamp(thumbLength, 1f, track.Height);
        float travel = Math.Max(0f, track.Height - length);
        if (travel <= 0f) return 0;
        float offset = Math.Clamp(pointerY - track.Y, 0f, travel);
        int scrollable = Math.Max(0, total - visible);
        return ClampFirst((int)MathF.Round(offset / travel * scrollable), total, visible);
    }

    /// <summary>
    /// The row a click on the track asks for: the row whose thumb position would put the pointer in the middle of
    /// the thumb, which is what makes clicking above or below the thumb a page-sized jump.
    /// </summary>
    public static int RowForTrackPoint(ToolInputRect track, float thumbLength, float pointerY, int total, int visible) =>
        RowForThumbTop(track, thumbLength, pointerY - Math.Clamp(thumbLength, 1f, Math.Max(1f, track.Height)) / 2f,
            total, visible);
}
