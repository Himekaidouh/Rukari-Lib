namespace Rukari.Lib.Tools;

/// <summary>One frame's pointer state, in physical screen pixels with the origin at the bottom left.</summary>
public readonly record struct ToolWindowPointer(float X, float Y, bool Pressed, bool Held);

/// <summary>Which gesture just finished, so the host can say in its log what the user did.</summary>
public enum ToolWindowGesture
{
    /// <summary>No gesture ended in this frame.</summary>
    None = 0,
    /// <summary>A title-band drag ended, so the window has a new position to remember.</summary>
    Move = 1,
    /// <summary>A resize-grip drag ended, so the window has a new size the user owns.</summary>
    Resize = 2
}

/// <summary>What one frame of pointer handling decided. Everything else the window's host does is drawing.</summary>
public readonly record struct ToolWindowInteractionResult(
    bool PlacementChanged,
    bool ResetRequested,
    bool CloseRequested,
    ToolWindowGesture Ended);

/// <summary>
/// The pointer state machine of one standalone tool window: which press starts a drag, which one resizes, which one
/// closes, and when a second press means "put it back".
///
/// <para>
/// It lives here, as pure arithmetic, because this is the part of a window that cannot be checked by looking at a
/// screenshot: whether the pointer keeps its grab offset, whether a release stores the new placement once, whether a
/// reset also gives the size back to the page. A rule that is wrong here looks like a window that "jumps" or
/// "sticks", and the only way to see it in a game is to drag it and watch. With the rules as a value the host feeds
/// one frame at a time, they are testable without a game at all.
/// </para>
///
/// <para>
/// The host owns two things the machine deliberately does not: whether the press belongs to this window at all (the
/// window stack answers that), and where a reset goes (the remembered position and the page's own size). Both arrive
/// as arguments or as a follow-up call, so nothing here needs a screen, a store or a page.
/// </para>
/// </summary>
public sealed class ToolWindowInteraction
{
    /// <summary>Window a second press on the title band is treated as the reset gesture.</summary>
    public const float DoubleClickSeconds = .35f;

    private ToolWindowPlacement _placement;
    private ToolWindowPlacement _resizeStart;
    private float _grabX, _grabY;
    private float _resizeFromX, _resizeFromY;
    private float _lastTitleClick;
    /// <summary>
    /// Whether this window offers a resize grip. The rule for resizing is implemented and tested either way; a host
    /// that does not draw a grip passes false, because a grip rectangle that is still honoured while nothing is drawn
    /// there would be an invisible corner that steals a click from the page underneath.
    /// </summary>
    private readonly bool _resizable;
    /// <summary>
    /// Whether a title-band press has happened at all. Without it the very first press would be compared against a
    /// clock that starts at zero and read as the second half of a double click — so a window opened in the first
    /// moments of a session would reset instead of dragging.
    /// </summary>
    private bool _titleClicked;
    private bool _dragging, _resizing, _userSized;
    /// <summary>Whether the gesture in progress has really moved or resized the window.</summary>
    private bool _gestureChanged;

    /// <summary>Starts a machine for one window, at the placement it is currently drawn at.</summary>
    public ToolWindowInteraction(ToolWindowPlacement placement) : this(placement, resizable: true)
    {
    }

    /// <summary>
    /// The same machine for a window that does not offer a resize grip: the grip is then neither chrome nor a starter
    /// of anything, so the corner belongs to the page.
    /// </summary>
    public ToolWindowInteraction(ToolWindowPlacement placement, bool resizable)
    {
        _placement = placement;
        _resizable = resizable;
    }

    /// <summary>Where the window is right now. The host draws from this and never keeps its own copy.</summary>
    public ToolWindowPlacement Placement => _placement;

    /// <summary>Whether a title-band drag is in progress.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Whether a resize-grip drag is in progress.</summary>
    public bool IsResizing => _resizing;

    /// <summary>
    /// Whether a drag or a resize is in progress, which the host uses to capture every pointer position until it
    /// ends.
    /// </summary>
    public bool IsBusy => _dragging || _resizing;

    /// <summary>
    /// True once the user has dragged the resize grip. Until then the window follows the size its page asks for;
    /// after that the size on screen is the user's, and only a reset gives it back.
    /// </summary>
    public bool UserSized => _userSized;

    /// <summary>Adopts a placement decided elsewhere: the default dock, a remembered position, or a reset.</summary>
    public void Adopt(ToolWindowPlacement placement) => _placement = placement;

    /// <summary>
    /// Hands the size back to the page, so the next <see cref="Adopt"/> of the page's own request takes effect again.
    /// Used by a reset: "back to the default" is a placement, and the size is part of it.
    /// </summary>
    public void ForgetUserSize() => _userSized = false;

    /// <summary>Pulls the window back inside a screen that changed size since it was placed.</summary>
    public bool KeepOnScreen(int pixelWidth, int pixelHeight)
    {
        ToolWindowPlacement clamped = ToolWindowLayout.Clamp(_placement, pixelWidth, pixelHeight);
        if (clamped == _placement) return false;
        _placement = clamped;
        return true;
    }

    /// <summary>
    /// Follows the size the page asks for, which is how a page with two views gets the second view's height instead
    /// of drawing outside the window it was given. Does nothing once the user has resized the window: from then on
    /// the size on screen is the one they chose.
    /// </summary>
    public bool FollowContentSize(float contentWidth, float contentHeight, int pixelWidth, int pixelHeight)
    {
        if (_userSized) return false;
        (float width, float height) = ToolWindowLayout.WindowSize(pixelWidth, pixelHeight, contentWidth, contentHeight);
        if (Math.Abs(width - _placement.Width) < .5f && Math.Abs(height - _placement.Height) < .5f) return false;
        _placement = ToolWindowLayout.Clamp(
            new ToolWindowPlacement(_placement.X, _placement.Y, width, height), pixelWidth, pixelHeight);
        return true;
    }

    /// <summary>
    /// Drops an unfinished gesture without storing anything. The host calls this when the window is hidden or torn
    /// down: the button that started the drag is gone with it, and a stale drag would move the window to wherever
    /// the pointer happens to be when it comes back.
    /// </summary>
    public void Cancel()
    {
        _dragging = false;
        _resizing = false;
        _gestureChanged = false;
    }

    /// <summary>
    /// Whether the window's own frame holds this point, which is what stops a press from also reaching a control the
    /// page drew underneath the chrome. The resize grip counts even though it is drawn over the page area.
    /// </summary>
    public bool ChromeHolds(float x, float y)
    {
        float localX = x - _placement.X;
        float localY = y - _placement.Y;
        return ToolWindowLayout.TitleBounds(_placement.Width, _placement.Height).Contains(localX, localY)
            || ToolWindowLayout.CloseBounds(_placement.Width, _placement.Height).Contains(localX, localY)
            || (_resizable && ToolWindowLayout.GripBounds(_placement.Width, _placement.Height).Contains(localX, localY));
    }

    /// <summary>
    /// One frame. <paramref name="ownedByThisWindow"/> is false when a window drawn above this one owns the press —
    /// the shared input guard is a union of rectangles and cannot say so, so the host asks the window stack and
    /// passes the answer in. <paramref name="now"/> is a monotonic clock in seconds, used for the double click.
    /// </summary>
    public ToolWindowInteractionResult Handle(ToolWindowPointer pointer, bool ownedByThisWindow, float now,
        int pixelWidth, int pixelHeight)
    {
        if (_dragging)
        {
            if (pointer.Held)
            {
                // The grab offset is what the pointer held when the drag started, so the window follows the hand
                // instead of snapping its corner to the cursor.
                ToolWindowPlacement moved = ToolWindowLayout.Clamp(new ToolWindowPlacement(
                    pointer.X - _grabX, pointer.Y - _grabY, _placement.Width, _placement.Height),
                    pixelWidth, pixelHeight);
                if (moved == _placement) return default;
                _placement = moved;
                _gestureChanged = true;
                return new ToolWindowInteractionResult(true, false, false, ToolWindowGesture.None);
            }
            _dragging = false;
            // Reported once, on release: the host stores and logs the new placement then, not on every frame, and a
            // press that never moved the window stores nothing.
            bool changed = _gestureChanged;
            _gestureChanged = false;
            return new ToolWindowInteractionResult(changed, false, false, ToolWindowGesture.Move);
        }
        if (_resizing)
        {
            if (pointer.Held)
            {
                ToolWindowPlacement resized = ToolWindowLayout.Resize(
                    _resizeStart, pointer.X - _resizeFromX, pointer.Y - _resizeFromY, pixelWidth, pixelHeight);
                if (resized == _placement) return default;
                _placement = resized;
                _gestureChanged = true;
                return new ToolWindowInteractionResult(true, false, false, ToolWindowGesture.None);
            }
            _resizing = false;
            // The size is the user's from now on, whether or not the drag really changed it: they reached for the
            // grip, and a page that then grew must not resize the window under their hand.
            _userSized = true;
            bool changed = _gestureChanged;
            _gestureChanged = false;
            return new ToolWindowInteractionResult(changed, false, false, ToolWindowGesture.Resize);
        }
        if (!pointer.Pressed || !ownedByThisWindow) return default;
        float localX = pointer.X - _placement.X;
        float localY = pointer.Y - _placement.Y;
        if (ToolWindowLayout.CloseBounds(_placement.Width, _placement.Height).Contains(localX, localY))
        {
            return new ToolWindowInteractionResult(false, false, CloseRequested: true, ToolWindowGesture.None);
        }
        if (_resizable && ToolWindowLayout.GripBounds(_placement.Width, _placement.Height).Contains(localX, localY))
        {
            _resizing = true;
            _resizeStart = _placement;
            _resizeFromX = pointer.X;
            _resizeFromY = pointer.Y;
            return default;
        }
        if (!ToolWindowLayout.TitleBounds(_placement.Width, _placement.Height).Contains(localX, localY)) return default;
        // A second press on the title band puts the window back where it opens by default. It must NOT also start a
        // drag — the pointer that reset the window is still holding the button — and the size goes back to the page
        // as well, because "default" is a placement, not only a position.
        if (_titleClicked && now - _lastTitleClick <= DoubleClickSeconds)
        {
            // A third press starts a drag again: the reset gesture is two clicks, not an endless chain.
            _titleClicked = false;
            _userSized = false;
            return new ToolWindowInteractionResult(false, ResetRequested: true, false, ToolWindowGesture.None);
        }
        _titleClicked = true;
        _lastTitleClick = now;
        _dragging = true;
        _gestureChanged = false;
        _grabX = pointer.X - _placement.X;
        _grabY = pointer.Y - _placement.Y;
        return default;
    }
}
