namespace Rukari.Lib.Tools;

/// <summary>
/// The concrete implementation behind <see cref="IToolPanelSurface"/>: it accumulates the controls one page
/// asked for during a single frame and answers that page's interaction questions about them.
///
/// Everything here is pure managed arithmetic, so the rules a mod depends on — a row is reserved before it is
/// used, an id is unique within a frame, a drag only continues while the press stays on the control it started
/// on — are unit-testable without a game, a canvas or a native call.
/// </summary>
public sealed class ToolPanelBuilder : IToolPanelSurface, IToolPanelSurfaceColours, IToolPanelSurfaceText,
    IToolPanelSurfaceTopDown, IToolPanelSurfaceStyledButtons
{
    private readonly List<ToolPanelElement> _elements = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolPanelElement> _byId = new(StringComparer.Ordinal);
    private readonly string? _pressedId;
    private float _cursor;
    private float _topCursor;

    /// <summary>
    /// Captures one frame. <paramref name="content"/> is the page's own rectangle in canvas units and
    /// <paramref name="pointer"/> is this frame's pointer already expressed in the same coordinates;
    /// <paramref name="pressedId"/> is the control the current press started on, which is what makes a drag keep
    /// belonging to the page even after the pointer leaves the rectangle.
    ///
    /// <paramref name="boundsInPixels"/> is the same rectangle in physical screen pixels. It is a separate
    /// argument rather than a derived value because the sheet may be scaled to fit the window and a page that
    /// places its own canvas must be told what it actually got. Without it the content rectangle is reported as
    /// its own pixel rectangle, which keeps a headless surface self-consistent.
    /// </summary>
    public ToolPanelBuilder(ToolInputRect content, ToolPanelPointer pointer, string? pressedId,
        ToolInputRect boundsInPixels)
    {
        Content = content;
        Pointer = pointer;
        BoundsInPixels = boundsInPixels;
        _pressedId = string.IsNullOrEmpty(pressedId) ? null : pressedId;
    }

    /// <summary>
    /// Captures one frame with no separate pixel rectangle; see the four-argument overload.
    /// </summary>
    public ToolPanelBuilder(ToolInputRect content, ToolPanelPointer pointer, string? pressedId = null)
        : this(content, pointer, pressedId, content, ToolPanelKeyboard.None)
    {
    }

    /// <summary>
    /// Captures one frame that also carries what the keyboard did, for a page with a focused text field.
    /// </summary>
    public ToolPanelBuilder(ToolInputRect content, ToolPanelPointer pointer, string? pressedId,
        ToolInputRect boundsInPixels, ToolPanelKeyboard keyboard)
        : this(content, pointer, pressedId, boundsInPixels)
    {
        TypedText = keyboard.Typed ?? string.Empty;
        Backspace = keyboard.Backspace;
        Submitted = keyboard.Submitted;
        Cancelled = keyboard.Cancelled;
        Delete = keyboard.Delete;
        MoveLeft = keyboard.MoveLeft;
        MoveRight = keyboard.MoveRight;
        MoveHome = keyboard.MoveHome;
        MoveEnd = keyboard.MoveEnd;
        SelectAll = keyboard.SelectAll;
        Composition = keyboard.Composition ?? string.Empty;
    }

    /// <summary>The page's content rectangle, in canvas units.</summary>
    public ToolInputRect Content { get; }

    /// <inheritdoc/>
    public ToolInputRect BoundsInPixels { get; }

    /// <summary>Every control asked for this frame, in the order it was asked for.</summary>
    public IReadOnlyList<ToolPanelElement> Elements => _elements;

    /// <summary>Total height consumed, so a renderer can report whether the page ran past its sheet.</summary>
    public float UsedHeight => _cursor;

    /// <summary>
    /// True while any interactive control is being held. The renderer publishes input capture for exactly these
    /// frames, so a drag that wanders over the official editor cannot click anything there.
    /// </summary>
    public bool IsDragging
    {
        get
        {
            if (_pressedId is null || !Pointer.Down) return false;
            foreach (ToolPanelElement element in _elements)
            {
                if (element.Kind == ToolPanelElementKind.Area && string.Equals(element.Id, _pressedId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public float Width => Content.Width;

    /// <inheritdoc/>
    public float Height => Content.Height;

    /// <inheritdoc/>
    public ToolPanelPointer Pointer { get; }

    /// <inheritdoc/>
    public ToolInputRect Row(float height)
    {
        float used = Math.Max(0f, height);
        var row = new ToolInputRect(Content.X, Content.Y + _cursor, Content.Width, used);
        _cursor += used;
        return row;
    }

    /// <inheritdoc/>
    public ToolInputRect Cell(ToolInputRect row, int index, int columns, float gap = 8f)
    {
        int count = Math.Clamp(columns, 1, 16);
        int slot = Math.Clamp(index, 0, count - 1);
        float spacing = Math.Max(0f, gap) * (count - 1);
        float width = Math.Max(1f, (row.Width - spacing) / count);
        return new ToolInputRect(row.X + slot * (width + Math.Max(0f, gap)), row.Y, width, row.Height);
    }

    /// <inheritdoc/>
    public void Space(float height) => _cursor += Math.Max(0f, height);

    /// <inheritdoc/>
    public ToolInputRect Band(float height)
    {
        float used = Math.Max(0f, height);
        _topCursor += used;
        return new ToolInputRect(Content.X, Content.Y + Content.Height - _topCursor, Content.Width, used);
    }

    /// <summary>
    /// How far the top-down cursor has come down, so a renderer can report two halves of a page meeting in the
    /// middle instead of overlapping silently.
    /// </summary>
    public float UsedFromTop => _topCursor;

    /// <inheritdoc/>
    public void Text(string id, string text, ToolInputRect bounds, int size = 18,
        ToolSurfaceStyle style = ToolSurfaceStyle.Normal) =>
        Add(id, ToolPanelElementKind.Text, bounds, text ?? "", style, size, true, false);

    /// <inheritdoc/>
    public void Status(string id, string text, ToolInputRect bounds) =>
        Add(id, ToolPanelElementKind.Status, bounds, text ?? "", ToolSurfaceStyle.Normal, 16, true, false);

    /// <inheritdoc/>
    public void Separator(string id) =>
        Add(id, ToolPanelElementKind.Separator, Row(ToolDrawerLayout.SeparatorHeight), "", ToolSurfaceStyle.Normal, 0, true, false);

    /// <inheritdoc/>
    public void Plate(string id, ToolInputRect bounds, ToolSurfaceStyle style = ToolSurfaceStyle.Solid, string? label = null) =>
        Add(id, ToolPanelElementKind.Plate, bounds, label ?? "", style, 18, true, false);

    /// <inheritdoc/>
    public bool Button(string id, string label, ToolInputRect bounds, bool enabled = true, bool highlighted = false)
    {
        Add(id, ToolPanelElementKind.Button, bounds, label ?? "", ToolSurfaceStyle.Normal, 18, enabled, highlighted);
        return enabled && WasClicked(id);
    }

    /// <inheritdoc/>
    public bool StyledButton(string id, string label, ToolInputRect bounds, ToolSurfaceStyle style,
        bool enabled, bool highlighted)
    {
        if (style is ToolSurfaceStyle.Panel or ToolSurfaceStyle.Header or ToolSurfaceStyle.Solid)
            style = ToolSurfaceStyle.Normal;
        // A disabled visual role must not leave an invisible hit target active in the previous-frame hit test.
        bool interactive = enabled && style != ToolSurfaceStyle.Disabled;
        Add(id, ToolPanelElementKind.Button, bounds, label ?? "", style, 18, interactive, highlighted);
        return interactive && WasClicked(id);
    }

    /// <inheritdoc/>
    public bool Area(string id, ToolInputRect bounds, bool interactive = true)
    {
        Add(id, ToolPanelElementKind.Area, bounds, "", ToolSurfaceStyle.Normal, 0, interactive, false);
        return interactive && IsHeld(id);
    }

    /// <inheritdoc/>
    public bool Swatch(string id, string colour, ToolInputRect bounds)
    {
        Add(id, ToolPanelElementKind.Swatch, bounds, "", ToolSurfaceStyle.Solid, 0, true, false, colour);
        return WasClicked(id);
    }

    /// <inheritdoc/>
    public bool TextField(string id, string text, ToolInputRect bounds, bool focused)
    {
        Add(id, ToolPanelElementKind.Field, bounds, text ?? "", ToolSurfaceStyle.Solid, 18, true, focused);
        if (focused) FocusedField = id;
        return WasClicked(id);
    }

    /// <summary>
    /// The field the page asked to focus this frame, or null when it has none. The renderer reads it to decide
    /// whether the keyboard belongs to the page, which is the one thing a page cannot arrange for itself.
    /// </summary>
    public string? FocusedField { get; private set; }

    /// <inheritdoc/>
    public string TypedText { get; } = string.Empty;

    /// <inheritdoc/>
    public bool Backspace { get; }

    /// <inheritdoc/>
    public bool Submitted { get; }

    /// <inheritdoc/>
    public bool Cancelled { get; }

    /// <inheritdoc/>
    public bool Delete { get; }

    /// <inheritdoc/>
    public bool MoveLeft { get; }

    /// <inheritdoc/>
    public bool MoveRight { get; }

    /// <inheritdoc/>
    public bool MoveHome { get; }

    /// <inheritdoc/>
    public bool MoveEnd { get; }

    /// <inheritdoc/>
    public bool SelectAll { get; }

    /// <inheritdoc/>
    public string Composition { get; } = string.Empty;

    /// <inheritdoc/>
    public bool IsHovered(string id) =>
        Find(id) is { Enabled: true } element && element.Bounds.Contains(Pointer.X, Pointer.Y);

    /// <inheritdoc/>
    public bool IsHeld(string id) =>
        string.Equals(_pressedId, id, StringComparison.Ordinal) && Pointer.Down && Find(id) is { Enabled: true };

    /// <inheritdoc/>
    public bool WasClicked(string id) =>
        string.Equals(_pressedId, id, StringComparison.Ordinal) && Pointer.Released && Find(id) is { Enabled: true }
        && Find(id)!.Bounds.Contains(Pointer.X, Pointer.Y);

    /// <inheritdoc/>
    public float DragX(string id) => IsHeld(id) ? Pointer.DeltaX : 0f;

    /// <inheritdoc/>
    public float DragY(string id) => IsHeld(id) ? Pointer.DeltaY : 0f;

    /// <summary>The control with this id, or null when the page did not ask for it this frame.</summary>
    public ToolPanelElement? Find(string id) =>
        id is not null && _byId.TryGetValue(id, out ToolPanelElement? element) ? element : null;

    /// <summary>
    /// The control a press that starts at this point belongs to, searched top-down so the last control asked for
    /// wins where two overlap. A renderer resolves a press against the previous frame's elements, because the
    /// press arrives before the page has drawn the frame that would contain it, and a page's layout is stable
    /// from one frame to the next.
    /// </summary>
    public static string? HitTest(IReadOnlyList<ToolPanelElement> elements, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(elements);
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            ToolPanelElement element = elements[i];
            // A field is clickable: that is how a page learns it should focus it. Leaving it out of this list is why
            // clicking a text field did nothing at all.
            if (element.Enabled
                && element.Kind is ToolPanelElementKind.Button or ToolPanelElementKind.Area
                    or ToolPanelElementKind.Swatch or ToolPanelElementKind.Field
                && element.Bounds.Contains(x, y))
            {
                return element.Id;
            }
        }
        return null;
    }

    /// <summary>The control in this frame's own layout that a press at this point belongs to.</summary>
    public string? HitTest(float x, float y) => HitTest(_elements, x, y);

    private void Add(string id, ToolPanelElementKind kind, ToolInputRect bounds, string text,
        ToolSurfaceStyle style, int size, bool enabled, bool highlighted, string? colour = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("A hosted control needs a nonempty id.");
        }
        if (!_ids.Add(id))
        {
            // Fail loudly instead of drawing a second control that would silently never receive input: the
            // renderer catches this per frame and shows the message on the panel, so a mod bug is visible.
            throw new InvalidOperationException($"The hosted control id '{id}' was used twice in one frame.");
        }
        var element = new ToolPanelElement(id, kind, bounds, text ?? "", style, size, enabled, highlighted)
        {
            Colour = colour
        };
        _elements.Add(element);
        _byId[id] = element;
    }
}
