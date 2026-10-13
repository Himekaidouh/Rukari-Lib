namespace Rukari.Lib.Tools;

/// <summary>
/// The conversions between a window's physical screen pixels and the coordinates its page draws in.
///
/// <para>
/// Three spaces meet here, and mixing two of them is the mistake this file exists to make impossible to repeat: the
/// page draws and is hit-tested in its own CONTENT coordinates (origin at the content rectangle's bottom-left), the
/// window's own chrome and its published input rectangle are in WINDOW-local units, and the screen is in physical
/// pixels. A page's control is drawn at its content position plus the window's padding, and a press is resolved by
/// subtracting the very same offset — one of these was once written with the padding applied twice, which drew every
/// control of a page shifted by the frame's padding and left its hit tests pointing somewhere else entirely.
/// </para>
///
/// <para>
/// All of it is arithmetic and all of it is here, so a host and a test compute the same answer from the same code
/// rather than from two copies of the same reasoning.
/// </para>
/// </summary>
public static class ToolWindowFrame
{
    /// <summary>
    /// The page's own coordinates for a physical screen point. A point outside the content rectangle comes back
    /// outside it, negative values included: the caller decides whether that is a hover, a miss or a drag.
    /// </summary>
    public static (float X, float Y) ContentPointer(ToolWindowPlacement placement, float screenX, float screenY) =>
        (screenX - (placement.X + ToolWindowLayout.Padding), screenY - (placement.Y + ToolWindowLayout.Padding));

    /// <summary>
    /// The physical screen point of a page coordinate, which is what a test or a diagnostic needs to aim at a control
    /// the page drew. It is exactly the inverse of <see cref="ContentPointer"/>.
    /// </summary>
    public static (float X, float Y) ScreenPoint(ToolWindowPlacement placement, float contentX, float contentY) =>
        (placement.X + ToolWindowLayout.Padding + contentX, placement.Y + ToolWindowLayout.Padding + contentY);

    /// <summary>
    /// Where a page control lands inside the window: its content rectangle moved by the window's padding. The window's
    /// own screen position is deliberately NOT added — a page control is a child of the window object, whose transform
    /// already applies it, so adding it here would draw the control one window away from the frame that contains it.
    /// </summary>
    public static ToolInputRect ElementBounds(ToolInputRect contentBounds) => new(
        contentBounds.X + ToolWindowLayout.Padding,
        contentBounds.Y + ToolWindowLayout.Padding,
        contentBounds.Width,
        contentBounds.Height);

    /// <summary>The physical screen rectangle of a window-local rectangle, which is what a hit test compares against.</summary>
    public static ToolInputRect ToScreenPixels(ToolWindowPlacement placement, ToolInputRect windowLocal) => new(
        placement.X + windowLocal.X,
        placement.Y + windowLocal.Y,
        windowLocal.Width,
        windowLocal.Height);
}
