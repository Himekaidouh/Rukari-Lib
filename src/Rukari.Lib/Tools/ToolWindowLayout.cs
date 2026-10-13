namespace Rukari.Lib.Tools;

/// <summary>
/// One tool window's rectangle, in physical screen pixels, measured from the BOTTOM-LEFT corner of the screen.
///
/// <para>
/// The toolbox drawer's sheet already stores its user-moved position this way, and for the same reason: Unity's
/// screen coordinates and the bottom-anchored (anchorMin/pivot = 0,0) RectTransforms the drawer builds both grow
/// upward from that corner, so a value can be handed to either without a second conversion that could disagree.
/// </para>
/// </summary>
public readonly record struct ToolWindowPlacement(float X, float Y, float Width, float Height)
{
    /// <summary>Whether this placement can be drawn: a window with no area would publish no input and be lost.</summary>
    public bool IsValid => Width >= 1f && Height >= 1f && float.IsFinite(X) && float.IsFinite(Y)
        && float.IsFinite(Width) && float.IsFinite(Height);

    /// <summary>The same rectangle as a screen-pixel hit rectangle.</summary>
    public ToolInputRect Rect => new(X, Y, Width, Height);
}

/// <summary>
/// Geometry of a standalone tool window: the one surface that is NOT the modal drawer sheet and can therefore be
/// moved, resized and kept beside it.
///
/// <para>
/// Everything here is pure arithmetic in canvas units. A window is drawn at a scale of one — unlike the drawer
/// sheet, which shrinks with the window — because the user resizes this window directly: a scale that followed the
/// screen would make dragging the grip move the contents by a different distance than the pointer travelled.
/// </para>
///
/// <para>
/// The default placement docks a new window to the bottom-left, clear of the official bottom bar and of the corner
/// decorations the drawer's own rail avoids. A wide, short page (a timeline) therefore comes up as a bottom strip
/// without asking for one, and a tall page starts in the same corner and is dragged where the user wants it.
/// </para>
/// </summary>
public static class ToolWindowLayout
{
    /// <summary>Inset of the window's content from its own frame, on every side.</summary>
    public const float Padding = 16f;

    /// <summary>Height of the title band. It carries the title, the close button, and is the window's drag handle.</summary>
    public const float HeaderHeight = 40f;

    /// <summary>
    /// Distance from the top padding down to the first content row: the title band plus the gap under it. The
    /// content rectangle a page receives is derived from this, so a page can measure the same window the renderer
    /// draws.
    /// </summary>
    public const float HeaderRowHeight = 48f;

    /// <summary>Side of the square close button.</summary>
    public const float CloseButtonSize = 32f;

    /// <summary>Side of the square resize grip in the window's bottom-right corner.</summary>
    public const float ResizeGripSize = 24f;

    /// <summary>Narrowest window: below this a page has no room for its own controls.</summary>
    public const float MinimumWidth = 360f;

    /// <summary>Shortest window: the title band, the padding and one row of content.</summary>
    public const float MinimumHeight = 200f;

    /// <summary>Widest window, as a fraction of the screen: the rest stays visible for the official editor.</summary>
    public const float MaximumWidthFraction = .92f;

    /// <summary>Tallest window, as a fraction of the screen.</summary>
    public const float MaximumHeightFraction = .82f;

    /// <summary>
    /// Widest a window opened for the first time may be, as a fraction of the screen. A page that asks for more
    /// still gets its own width when the user drags the grip; this only bounds what the library chooses.
    /// </summary>
    public const float DefaultWidthFraction = .68f;

    /// <summary>Height a window opens with when its page states no preference.</summary>
    public const float DefaultHeight = 340f;

    /// <summary>Width a window opens with when its page states no preference.</summary>
    public const float DefaultWidth = 720f;

    /// <summary>Gap kept from the screen's left and right edges.</summary>
    public const float ScreenMargin = 12f;

    /// <summary>
    /// Gap kept below a docked window. It clears the official bottom bar and the decorations the drawer's rail
    /// also keeps away from, so a default placement never covers an official control.
    /// </summary>
    public const float DockBottomMargin = 44f;

    /// <summary>Widest window the screen allows.</summary>
    public static float MaximumWidth(int pixelWidth) =>
        Math.Max(MinimumWidth, Math.Max(1, pixelWidth) * MaximumWidthFraction);

    /// <summary>Tallest window the screen allows.</summary>
    public static float MaximumHeight(int pixelHeight) =>
        Math.Max(MinimumHeight, Math.Max(1, pixelHeight) * MaximumHeightFraction);

    /// <summary>
    /// Size a window opens with: the page's own request when it is a usable number, and the shared default
    /// otherwise. Zero, a negative value and a non-finite value all mean "no opinion", which is exactly what a
    /// page that does not implement <see cref="IToolPanelSizing"/> reports.
    ///
    /// <para>
    /// The request is a CONTENT size, matching what the drawer hands the same page, so a page that looks right
    /// inside the drawer keeps its own coordinate space here. The chrome — padding and title band — is added on
    /// top, and the result is clamped to the screen.
    /// </para>
    /// </summary>
    public static (float Width, float Height) WindowSize(
        int pixelWidth, int pixelHeight, float preferredWidth, float preferredHeight)
    {
        float contentWidth = float.IsFinite(preferredWidth) && preferredWidth > 0 ? preferredWidth : DefaultWidth;
        float contentHeight = float.IsFinite(preferredHeight) && preferredHeight > 0 ? preferredHeight : DefaultHeight;
        float width = contentWidth + Padding * 2;
        float height = contentHeight + Padding * 2 + HeaderRowHeight;
        // A page that asks for more than the screen gets the screen: a window larger than the display could not be
        // dragged back into view.
        return (Math.Clamp(width, MinimumWidth, MaximumWidth(pixelWidth)),
            Math.Clamp(height, MinimumHeight, MaximumHeight(pixelHeight)));
    }

    /// <summary>
    /// Where a window opens: docked to the bottom-left with a margin that clears the official bottom bar. The
    /// width is the smaller of what the page asked for and <see cref="DefaultWidthFraction"/> of the screen, so a
    /// page requesting a very wide window still comes up as a panel the user can see beside the workspace.
    /// </summary>
    public static ToolWindowPlacement DefaultPlacement(
        int pixelWidth, int pixelHeight, float preferredWidth, float preferredHeight)
    {
        (float width, float height) = WindowSize(pixelWidth, pixelHeight, preferredWidth, preferredHeight);
        // The cap applies to every page, including one with no width opinion: a window opened for the first time
        // must leave the workspace visible whatever the page asked for or failed to ask for.
        float docked = Math.Max(MinimumWidth, Math.Max(1, pixelWidth) * DefaultWidthFraction);
        width = Math.Min(width, docked);
        float x = Math.Clamp(ScreenMargin, 0f, Math.Max(0f, pixelWidth - width));
        float y = DockedBottom(pixelHeight, height);
        return Clamp(new ToolWindowPlacement(x, y, width, height), pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Bottom edge of a docked window. Kept inside the screen on a display too short for the margin, because the
    /// bottom edge is where the resize grip lives: a window pushed past it could not be resized back.
    /// </summary>
    public static float DockedBottom(int pixelHeight, float height) =>
        Math.Max(0f, Math.Min(DockBottomMargin, Math.Max(0f, pixelHeight - height)));

    /// <summary>
    /// Keeps a window reachable: fully inside the screen, and never smaller than the minimum. Applied every frame,
    /// so a window placed on a larger display and then re-opened on a smaller one is pulled back into view instead
    /// of being drawn where it cannot be seen or grabbed.
    /// </summary>
    public static ToolWindowPlacement Clamp(ToolWindowPlacement placement, int pixelWidth, int pixelHeight)
    {
        float width = Math.Clamp(
            float.IsFinite(placement.Width) ? placement.Width : MinimumWidth,
            MinimumWidth, MaximumWidth(pixelWidth));
        float height = Math.Clamp(
            float.IsFinite(placement.Height) ? placement.Height : MinimumHeight,
            MinimumHeight, MaximumHeight(pixelHeight));
        float maxX = Math.Max(0f, pixelWidth - width);
        float maxY = Math.Max(0f, pixelHeight - height);
        return new ToolWindowPlacement(
            Math.Clamp(float.IsFinite(placement.X) ? placement.X : 0f, 0f, maxX),
            Math.Clamp(float.IsFinite(placement.Y) ? placement.Y : 0f, 0f, maxY),
            width, height);
    }

    /// <summary>
    /// The size a drag of the bottom-right grip asks for. The bottom-left corner is the anchor the window was
    /// placed by, so the pointer's horizontal travel widens the window and its vertical travel makes it taller;
    /// both are clamped, and the result can be passed straight to <see cref="Clamp"/>.
    /// </summary>
    public static ToolWindowPlacement Resize(
        ToolWindowPlacement start, float dragX, float dragY, int pixelWidth, int pixelHeight) => Clamp(
        new ToolWindowPlacement(start.X, start.Y, start.Width + dragX, start.Height + dragY),
        pixelWidth, pixelHeight);

    /// <summary>
    /// Window-local rectangle of the title band, measured from the window's bottom-left corner with
    /// <paramref name="height"/> as the drawn height. It is both the visible heading and the drag handle, and it
    /// stops short of the close button so a press on either is unambiguous.
    /// </summary>
    public static ToolInputRect TitleBounds(float width, float height) => new(
        Padding,
        Math.Max(0f, height - Padding - HeaderHeight),
        Math.Max(1f, width - Padding * 2 - CloseButtonSize - 8f),
        HeaderHeight);

    /// <summary>Window-local rectangle of the close button, inset by the window's own padding.</summary>
    public static ToolInputRect CloseBounds(float width, float height) => new(
        Math.Max(0f, width - Padding - CloseButtonSize),
        Math.Max(0f, height - Padding - CloseButtonSize),
        CloseButtonSize, CloseButtonSize);

    /// <summary>Window-local rectangle of the resize grip, a square in the bottom-right corner.</summary>
    public static ToolInputRect GripBounds(float width, float height) => new(
        Math.Max(0f, width - Padding - ResizeGripSize),
        Padding,
        ResizeGripSize, ResizeGripSize);

    /// <summary>
    /// Window-local rectangle handed to the page as its coordinate space: the window minus its padding and the
    /// title band. It is exactly the content size the page asked for whenever the window was not clamped.
    /// </summary>
    public static ToolInputRect ContentBounds(float width, float height) => new(
        Padding, Padding,
        Math.Max(1f, width - Padding * 2),
        Math.Max(1f, height - Padding * 2 - HeaderRowHeight));

    /// <summary>
    /// The same content rectangle in physical screen pixels, which is what a page that keeps its own scaled canvas
    /// needs instead of repeating the conversion. The window is drawn at a scale of one, so this is the content
    /// rectangle moved to the window's screen position.
    /// </summary>
    public static ToolInputRect ContentPixels(ToolWindowPlacement placement) => new(
        placement.X + Padding,
        placement.Y + Padding,
        Math.Max(1f, placement.Width - Padding * 2),
        Math.Max(1f, placement.Height - Padding * 2 - HeaderRowHeight));

    /// <summary>
    /// Whether a screen-pixel point is inside the window at all. Used to decide which of two overlapping windows
    /// a press belongs to: the input guard is a union of rectangles with no notion of stacking order, so the
    /// hosts, not the guard, must agree on who is on top.
    /// </summary>
    public static bool Contains(ToolWindowPlacement placement, float x, float y) =>
        placement.IsValid && placement.Rect.Contains(x, y);
}

/// <summary>
/// Remembers where the user put each window, for as long as the library lives.
///
/// <para>
/// Session-scoped on purpose: a position is worth keeping while the game is running, and writing it to disk would
/// need a file this library does not own and a migration rule for every future window size. A placement is stored
/// in physical pixels, so it is re-clamped against the current screen on the way out rather than trusted; the
/// entry a window really uses is the clamped one, which is what keeps a stale entry from hiding a window off
/// screen after a resolution change.
/// </para>
/// </summary>
public sealed class ToolWindowPlacementStore
{
    private readonly Dictionary<string, ToolWindowPlacement> _placements = new(StringComparer.Ordinal);

    /// <summary>Windows remembered right now; a diagnostic aid and a bound for the dictionary.</summary>
    public int Count => _placements.Count;

    /// <summary>
    /// The placement for a window, clamped to this screen, or false when the user has never moved or resized it
    /// and the caller should derive the default instead.
    /// </summary>
    public bool TryGet(string windowId, int pixelWidth, int pixelHeight, out ToolWindowPlacement placement)
    {
        if (windowId is not null && _placements.TryGetValue(windowId, out ToolWindowPlacement stored))
        {
            placement = ToolWindowLayout.Clamp(stored, pixelWidth, pixelHeight);
            return true;
        }
        placement = default;
        return false;
    }

    /// <summary>Stores where the user put a window. Called while dragging and resizing, not every frame.</summary>
    public void Remember(string windowId, ToolWindowPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        if (_placements.Count >= 64 && !_placements.ContainsKey(windowId)) return;
        _placements[windowId] = placement;
    }

    /// <summary>
    /// Forgets a window's position, so it opens at its default place again. Returns whether there was anything to
    /// forget, which is what lets the reset gesture be silent the second time.
    /// </summary>
    public bool Reset(string windowId) =>
        !string.IsNullOrWhiteSpace(windowId) && _placements.Remove(windowId);

    /// <summary>Forgets every position; used when the library is torn down.</summary>
    public void Clear() => _placements.Clear();
}
