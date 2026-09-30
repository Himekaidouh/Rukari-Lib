namespace Rukari.Lib.Tools;

/// <summary>
/// Visual style of a hosted control. A mod names a role, never a colour or a texture, so every page keeps the
/// shared theme and a future restyle does not have to touch any mod.
/// </summary>
public enum ToolSurfaceStyle
{
    /// <summary>Body text and ordinary buttons.</summary>
    Normal,
    /// <summary>The one primary action of a page.</summary>
    Primary,
    /// <summary>An active/selected row or toggle.</summary>
    Selected,
    /// <summary>Unavailable, but still visible.</summary>
    Disabled,
    /// <summary>The shared undo affordance.</summary>
    Undo,
    /// <summary>A quiet plate that sits on the panel colour.</summary>
    Panel,
    /// <summary>A raised plate, used for headers and preview frames.</summary>
    Header,
    /// <summary>A neutral solid plate with no accent.</summary>
    Solid
}

/// <summary>What a hosted element is, so the renderer knows which control pool it needs.</summary>
public enum ToolPanelElementKind
{
    /// <summary>A text block with no background.</summary>
    Text,
    /// <summary>A smaller, dimmer text block.</summary>
    Status,
    /// <summary>A clickable button with a label.</summary>
    Button,
    /// <summary>A plate with no text, optionally labelled.</summary>
    Plate,
    /// <summary>A one-pixel divider across the content width.</summary>
    Separator,
    /// <summary>A surface the page hit-tests itself, used for drags such as a character handle.</summary>
    Area,
    /// <summary>
    /// A rectangle filled with a colour the page chose, which is what a colour picker is built from. Every other
    /// kind draws with the shared theme, and a caller that wants to show the colour it is about to apply has no way
    /// to do it otherwise.
    /// </summary>
    Swatch,

    /// <summary>
    /// A text field a page can type into. Drawn like a plate with left-aligned text, and marked focused by the page
    /// rather than by the renderer, because only the page knows which of its fields the user is editing.
    /// </summary>
    Field
}

/// <summary>
/// Maps <see cref="ToolSurfaceStyle"/> onto the shared palette keys. A hosted page names a role and the library
/// decides the colour, so a restyle is one change here instead of one per mod.
/// </summary>
public static class ToolSurfaceStyles
{
    /// <summary>The palette key a style is drawn with. Total: every style resolves to a drawable surface.</summary>
    public static string ThemeKey(ToolSurfaceStyle style) => style switch
    {
        ToolSurfaceStyle.Primary => "button.primary",
        ToolSurfaceStyle.Selected => "button.selected",
        ToolSurfaceStyle.Disabled => "button.disabled",
        ToolSurfaceStyle.Undo => "button.undo",
        ToolSurfaceStyle.Panel => "panel",
        ToolSurfaceStyle.Header => "header",
        ToolSurfaceStyle.Solid => "solid",
        _ => "button"
    };
}

/// <summary>
/// One frame of pointer state inside a hosted page, in the page's own canvas units with the origin at the
/// content's bottom-left corner. Physical screen pixels are converted by the renderer, so a mod never has to
/// know about the canvas scale or the game's own UI scale.
/// </summary>
public readonly record struct ToolPanelPointer(
    float X,
    float Y,
    bool Down,
    bool Pressed,
    bool Released,
    float DeltaX,
    float DeltaY)
{
    /// <summary>No pointer at all, for a headless or off-screen frame.</summary>
    public static readonly ToolPanelPointer None = new(0f, 0f, false, false, false, 0f, 0f);
}

/// <summary>One control a hosted page asked the shared panel to draw this frame.</summary>
public sealed record ToolPanelElement(
    string Id,
    ToolPanelElementKind Kind,
    ToolInputRect Bounds,
    string Text,
    ToolSurfaceStyle Style,
    int Size,
    bool Enabled,
    bool Highlighted)
{
    /// <summary>
    /// The colour a <see cref="ToolPanelElementKind.Swatch"/> is filled with, as <c>#RRGGBB</c> or
    /// <c>#RRGGBBAA</c>. Null for every other kind, which draw with the shared theme.
    /// </summary>
    public string? Colour { get; init; }
}

/// <summary>
/// Optional half of the drawing surface: drawing a colour the page chose. The shared theme deliberately knows only
/// named roles, so a page that offers a colour picker needs one primitive that takes a colour — and, like the other
/// optional halves, it arrives as a sibling interface rather than as another member of a frozen one. A page asks for
/// it with <c>surface as IToolPanelSurfaceColours</c> and keeps working without it.
/// </summary>
public interface IToolPanelSurfaceColours
{
    /// <summary>
    /// Draws a rectangle filled with an arbitrary colour and returns true on the frame it is clicked. The colour is
    /// written the way a style sheet writes it, so a page and its own file never disagree about a shade.
    /// </summary>
    bool Swatch(string id, string colour, ToolInputRect bounds);
}

/// <summary>What the keyboard did this frame, handed to a page that owns a focused text field.</summary>
public readonly record struct ToolPanelKeyboard(
    string Typed,
    bool Backspace,
    bool Submitted,
    bool Cancelled,
    bool Delete = false,
    bool MoveLeft = false,
    bool MoveRight = false,
    bool MoveHome = false,
    bool MoveEnd = false,
    bool SelectAll = false,
    string? Composition = null)
{
    /// <summary>Nothing typed, which is what a page sees before it focuses a field.</summary>
    public static readonly ToolPanelKeyboard None = new(string.Empty, false, false, false);
}

/// <summary>
/// Optional half of the drawing surface: laying a page out from the top down. <see cref="IToolPanelSurface.Row"/>
/// reserves bands upward from the bottom of the sheet, which is how a page reports what it wants to say — read in
/// the order the bands are asked for — but it draws that order bottom-up, so a page that simply calls Row in
/// reading order appears upside down. A page that wants its heading at the top asks for bands here instead.
/// </summary>
public interface IToolPanelSurfaceTopDown
{
    /// <summary>
    /// Reserves a full-width band of the given height from the top of the sheet and moves the top cursor down past
    /// it. A page may mix the two directions — a footer pinned to the bottom, sections stacked from the top — as
    /// long as the two do not meet.
    /// </summary>
    ToolInputRect Band(float height);
}

/// <summary>
/// Optional half of the drawing surface: a text field the user can type into. The library owns the keyboard — it is
/// the only owner of the game's input — so a page cannot read the keyboard itself and instead asks for a field and
/// reads back what arrived. The page keeps the value; the library only reports the keystrokes, which is what keeps a
/// half-typed line from being written anywhere until the page decides to write it.
/// </summary>
public interface IToolPanelSurfaceText
{
    /// <summary>
    /// Draws a text field and returns true on the frame it is clicked, which is how a page learns it should focus
    /// this field. <paramref name="focused"/> is the page's own state and is what makes the library capture the
    /// keyboard for it.
    /// </summary>
    bool TextField(string id, string text, ToolInputRect bounds, bool focused);

    /// <summary>Characters typed since the previous frame, empty unless one of the page's fields is focused.</summary>
    string TypedText { get; }

    /// <summary>Whether backspace was pressed since the previous frame.</summary>
    bool Backspace { get; }

    /// <summary>Whether Enter was pressed since the previous frame.</summary>
    bool Submitted { get; }

    /// <summary>Whether Escape was pressed since the previous frame.</summary>
    bool Cancelled { get; }

    /// <summary>Whether the delete key was pressed since the previous frame.</summary>
    bool Delete { get; }

    /// <summary>Whether the caret was moved to the left since the previous frame.</summary>
    bool MoveLeft { get; }

    /// <summary>Whether the caret was moved to the right since the previous frame.</summary>
    bool MoveRight { get; }

    /// <summary>Whether the caret was sent to the start of the line since the previous frame.</summary>
    bool MoveHome { get; }

    /// <summary>Whether the caret was sent to the end of the line since the previous frame.</summary>
    bool MoveEnd { get; }

    /// <summary>Whether everything was selected since the previous frame, which is what Ctrl+A means.</summary>
    bool SelectAll { get; }

    /// <summary>
    /// The characters an input method is still composing, or empty. They are shown at the caret but are not part of
    /// the text: the input method may still replace them, and only what it commits arrives in
    /// <see cref="TypedText"/>.
    /// </summary>
    string Composition { get; }
}

/// <summary>
/// Optional half of the drawing surface: a button with a shared visual role. A page asks for it with
/// <c>surface as IToolPanelSurfaceStyledButtons</c> and may use the original
/// <see cref="IToolPanelSurface.Button"/> when a surface does not provide it. This type was introduced in
/// API 0.2.3: checking the optional capability does not allow loading against a 0.2.2 contract DLL.
/// The frozen surface interface keeps its original button method and signature.
/// </summary>
public interface IToolPanelSurfaceStyledButtons
{
    /// <summary>
    /// Draws a button and returns true on the frame it is clicked. Normal is neutral, Primary is the decorated
    /// commit action, Undo is the decorated secondary action, and Selected is a persistent selected state.
    /// Disabled is always noninteractive. Panel, Header and Solid are plate roles and draw as neutral buttons.
    /// <paramref name="enabled"/> and <paramref name="highlighted"/> retain the original button's meanings.
    /// </summary>
    bool StyledButton(string id, string label, ToolInputRect bounds, ToolSurfaceStyle style,
        bool enabled, bool highlighted);
}

/// <summary>
/// A page whose content is drawn by the mod through the shared panel instead of being described by a
/// <see cref="ToolPageSnapshot"/>. The library owns the canvas, the frame, the title, the close button, the
/// theme and all input ownership; the mod only asks for controls inside the content rectangle. A page must not
/// create its own canvas or game objects: that is what made the older editors fight the official UI for input
/// and made every mod look different.
///
/// <para>
/// THIS INTERFACE IS FROZEN. A mod implements it, and a type that no longer implements every member of an
/// interface it declares fails to LOAD — the mod disappears with a TypeLoadException instead of failing one
/// call. So a new capability never becomes another member here: it goes into a sibling interface the page may
/// implement as well (<see cref="IToolPanelSizing"/>, <see cref="IToolPanelLifecycle"/>). This is the same rule
/// as "never add an optional parameter", applied to interfaces.
/// </para>
/// </summary>
public interface IToolPanelContent
{
    /// <summary>
    /// Height of the sheet this page wants, in canvas units before the panel scale is applied. The renderer
    /// shrinks it to fit the window exactly like a snapshot page, so returning a generous value is safe.
    /// </summary>
    float PreferredHeight { get; }

    /// <summary>
    /// Draws one frame. Called on the game main thread, only while this page is the open leaf, and only when
    /// the node editor is visible. Implementations must be free of native side effects they cannot undo.
    /// </summary>
    void Draw(IToolPanelSurface surface);
}

/// <summary>
/// Optional half of a hosted page's sizing: a page that wants a sheet wider or narrower than the shared default
/// implements this beside <see cref="IToolPanelContent"/>. It is a separate interface because a page that is
/// happy with the default sheet must not be forced to answer, and because the frozen interface above must be
/// able to stay frozen.
/// </summary>
public interface IToolPanelSizing
{
    /// <summary>
    /// Width of the sheet this page wants, in canvas units before the panel scale is applied. Zero, a negative
    /// value or a non-finite value all mean "the shared default sheet". The renderer clamps the result to a
    /// usable range and still shrinks the sheet to the window, so a page must read
    /// <see cref="IToolPanelSurface.BoundsInPixels"/> rather than predict what it received.
    /// </summary>
    float PreferredWidth { get; }
}

/// <summary>
/// Optional half of a hosted page: what a page that owns native or expensive state wants to know about becoming
/// visible. A page that only draws has nothing to do here.
///
/// <para>
/// The callbacks are paired and never overlap: <see cref="OnShown"/> runs once before the first
/// <see cref="IToolPanelContent.Draw"/> of a showing, and <see cref="OnHidden"/> once after the panel stops
/// showing — including when the user collapses a level, switches to another page, leaves the node editor, or the
/// toolbox is thrown away. A page must therefore treat every frame between them as "I am on screen" and must
/// release whatever it built in <see cref="OnHidden"/>. An exception from either callback is caught and logged
/// by the renderer and never takes the shared toolbox down.
/// </para>
/// </summary>
public interface IToolPanelLifecycle
{
    /// <summary>Called on the game main thread when this page's panel becomes the visible leaf.</summary>
    void OnShown();

    /// <summary>
    /// Called on the game main thread when this page's panel stops being visible, exactly once per
    /// <see cref="OnShown"/>. The content rectangle is no longer valid when this runs.
    /// </summary>
    void OnHidden();
}

/// <summary>
/// The drawing surface handed to a hosted page for one frame. Everything is placed in an explicit rectangle, so
/// layout is ordinary arithmetic the mod owns and the library never has to guess. Coordinates are the page's own
/// canvas units with the origin at the content's bottom-left corner, and Y grows upward.
/// </summary>
public interface IToolPanelSurface
{
    /// <summary>Usable content width.</summary>
    float Width { get; }

    /// <summary>Usable content height.</summary>
    float Height { get; }

    /// <summary>
    /// The very same content rectangle in physical screen pixels, with its origin at the screen's bottom-left
    /// corner. A page that keeps its own scaled canvas — an editor whose coordinates are authored against the
    /// game's own reference resolution, for instance — places that canvas from this rectangle instead of
    /// repeating the renderer's canvas arithmetic, which is what keeps the two from disagreeing by a few pixels.
    /// The sheet may be shrunk to fit the window, so <see cref="Width"/> and <c>BoundsInPixels.Width</c> are not
    /// a fixed ratio.
    /// </summary>
    ToolInputRect BoundsInPixels { get; }

    /// <summary>This frame's pointer, already converted into content coordinates.</summary>
    ToolPanelPointer Pointer { get; }

    /// <summary>Reserves a full-width band of the given height and advances the cursor above it.</summary>
    ToolInputRect Row(float height);

    /// <summary>Splits a row into equal columns. A pure function, so it can be called repeatedly.</summary>
    ToolInputRect Cell(ToolInputRect row, int index, int columns, float gap = 8f);

    /// <summary>Advances the cursor without drawing anything.</summary>
    void Space(float height);

    /// <summary>Draws a text block. The caller sizes the rectangle.</summary>
    void Text(string id, string text, ToolInputRect bounds, int size = 18,
        ToolSurfaceStyle style = ToolSurfaceStyle.Normal);

    /// <summary>Draws a small dim status line.</summary>
    void Status(string id, string text, ToolInputRect bounds);

    /// <summary>Draws a divider across the content width at the cursor and advances past it.</summary>
    void Separator(string id);

    /// <summary>Draws a plate with no interaction.</summary>
    void Plate(string id, ToolInputRect bounds, ToolSurfaceStyle style = ToolSurfaceStyle.Solid, string? label = null);

    /// <summary>Draws a button and returns true on the frame it is clicked.</summary>
    bool Button(string id, string label, ToolInputRect bounds, bool enabled = true, bool highlighted = false);

    /// <summary>
    /// Draws an area the page hit-tests itself. Returns true while the pointer is held on it, which is how a
    /// drag continues; the renderer keeps consuming input for that whole time.
    /// </summary>
    bool Area(string id, ToolInputRect bounds, bool interactive = true);

    /// <summary>Whether the pointer is over this control.</summary>
    bool IsHovered(string id);

    /// <summary>Whether the pointer is being held on this control, including before the first drag threshold.</summary>
    bool IsHeld(string id);

    /// <summary>Whether a press and its release both landed on this control.</summary>
    bool WasClicked(string id);

    /// <summary>Horizontal pointer movement this frame while this control is held.</summary>
    float DragX(string id);

    /// <summary>Vertical pointer movement this frame while this control is held.</summary>
    float DragY(string id);
}
