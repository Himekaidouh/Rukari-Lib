using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The window's pointer rules, which a screenshot cannot show and a game can only show by being dragged once. Every
/// rule here is one a user would describe as "the window jumped" or "the window stuck" if it were wrong.
/// </summary>
internal static class ToolWindowInteractionTests
{
    private static ToolWindowInteraction NewWindow() =>
        new(new ToolWindowPlacement(100f, 200f, 600f, 400f));

    /// <summary>A point inside the title band of the window above, and one inside the page area.</summary>
    private static (float X, float Y) InTitle(ToolWindowPlacement placement) =>
        (placement.X + ToolWindowLayout.Padding + 40f, placement.Y + placement.Height - ToolWindowLayout.Padding - 20f);

    private static (float X, float Y) InPage(ToolWindowPlacement placement) =>
        (placement.X + placement.Width / 2f, placement.Y + 80f);

    private static (float X, float Y) InGrip(ToolWindowPlacement placement) =>
        (placement.X + placement.Width - ToolWindowLayout.Padding - 8f, placement.Y + ToolWindowLayout.Padding + 8f);

    internal static void DraggingTheTitleKeepsTheGrabOffsetAndNeverResizes()
    {
        var window = NewWindow();
        ToolWindowPlacement start = window.Placement;
        (float x, float y) = InTitle(start);
        float grabX = x - start.X, grabY = y - start.Y;
        ToolWindowInteractionResult press = window.Handle(new ToolWindowPointer(x, y, true, true), true, 0f, 2560, 1440);
        Check.True(window.IsDragging, "A press on the title band starts a drag.");
        Check.True(!press.CloseRequested && !press.ResetRequested, "A single press is neither a close nor a reset.");

        // Moving the hand by (120, -60) moves the window by exactly that, keeping the point under the cursor.
        window.Handle(new ToolWindowPointer(x + 120f, y - 60f, false, true), true, .05f, 2560, 1440);
        Check.Equal(start.X + 120f, window.Placement.X);
        Check.Equal(start.Y - 60f, window.Placement.Y);
        Check.Equal(start.Width, window.Placement.Width, "A drag must never change the size.");
        Check.Equal(start.Height, window.Placement.Height, "A drag must never change the size.");
        Check.Equal(grabX, x + 120f - window.Placement.X);
        Check.Equal(grabY, y - 60f - window.Placement.Y);

        // Releasing stores it once, and only because it really moved.
        ToolWindowInteractionResult release = window.Handle(new ToolWindowPointer(x + 120f, y - 60f, false, false), true, .1f, 2560, 1440);
        Check.True(release.PlacementChanged, "A drag that moved the window must report one placement to store.");
        Check.True(!window.IsDragging, "The drag ends on release.");
        Check.True(!window.IsBusy, "Nothing is in progress after the release.");
        ToolWindowInteractionResult again = window.Handle(new ToolWindowPointer(x + 120f, y - 60f, false, false), true, .2f, 2560, 1440);
        Check.True(!again.PlacementChanged, "A released window must not keep reporting changes.");
    }

    internal static void APressThatNeverMovedStoresNothing()
    {
        var window = NewWindow();
        (float x, float y) = InTitle(window.Placement);
        window.Handle(new ToolWindowPointer(x, y, true, true), true, 0f, 2560, 1440);
        ToolWindowInteractionResult release = window.Handle(new ToolWindowPointer(x, y, false, false), true, .1f, 2560, 1440);
        Check.True(!release.PlacementChanged, "A click on the title band that moved nothing stores nothing.");
        Check.True(!window.IsDragging, "A click still ends its own drag.");
    }

    internal static void DraggingTheGripResizesFromThePressAndTakesTheSizeOver()
    {
        var window = NewWindow();
        ToolWindowPlacement start = window.Placement;
        (float x, float y) = InGrip(start);
        window.Handle(new ToolWindowPointer(x, y, true, true), true, 0f, 2560, 1440);
        Check.True(window.IsResizing, "A press on the grip starts a resize.");
        Check.True(!window.IsDragging, "The grip is not the drag handle.");

        // The bottom-left corner is the anchor, so horizontal travel widens and vertical travel makes it taller.
        window.Handle(new ToolWindowPointer(x + 200f, y + 100f, false, true), true, .05f, 2560, 1440);
        Check.Equal(start.X, window.Placement.X, "A resize must not move the anchored corner.");
        Check.Equal(start.Y, window.Placement.Y, "A resize must not move the anchored corner.");
        Check.Equal(start.Width + 200f, window.Placement.Width);
        Check.Equal(start.Height + 100f, window.Placement.Height);

        window.Handle(new ToolWindowPointer(x + 200f, y + 100f, false, false), true, .1f, 2560, 1440);
        Check.True(!window.IsResizing, "The resize ends on release.");
        Check.True(window.UserSized, "After the grip is used the size belongs to the user.");

        // And from then on the page's own request cannot resize the window under their hand.
        Check.True(!window.FollowContentSize(400f, 900f, 2560, 1440),
            "A user-sized window must stop following the page's requested size.");
        Check.Equal(start.Width + 200f, window.Placement.Width);

        // A reset hands the size back, which is what makes "back to the default" mean the whole placement.
        window.ForgetUserSize();
        Check.True(window.FollowContentSize(400f, 900f, 2560, 1440),
            "After a reset the window follows the page's size again.");
        Check.Equal(400f + ToolWindowLayout.Padding * 2, window.Placement.Width);
    }

    internal static void DoubleClickingTheTitleResetsInsteadOfDragging()
    {
        var window = NewWindow();
        ToolWindowPlacement start = window.Placement;
        (float x, float y) = InTitle(start);
        window.Handle(new ToolWindowPointer(x, y, true, true), true, 0f, 2560, 1440);
        window.Handle(new ToolWindowPointer(x, y, false, false), true, .1f, 2560, 1440);
        ToolWindowInteractionResult second = window.Handle(new ToolWindowPointer(x, y, true, true), true, .2f, 2560, 1440);
        Check.True(second.ResetRequested, "A second press inside the double-click window is the reset gesture.");
        Check.True(!window.IsDragging, "The press that reset the window must not also start a drag.");
        // A reset gives the size back to the page as well: "default" is a placement, not only a position.
        Check.True(!window.UserSized, "A reset must hand the size back to the page.");

        // Two presses far apart in time are two clicks, and the second one starts a drag.
        var slow = NewWindow();
        slow.Handle(new ToolWindowPointer(x, y, true, true), true, 10f, 2560, 1440);
        slow.Handle(new ToolWindowPointer(x, y, false, false), true, 10.1f, 2560, 1440);
        ToolWindowInteractionResult late = slow.Handle(new ToolWindowPointer(x, y, true, true), true, 10.5f, 2560, 1440);
        Check.True(!late.ResetRequested, "A slow second click is not a reset.");
        Check.True(slow.IsDragging, "A slow second click starts a drag like any other press on the title band.");
    }

    internal static void APressOwnedByAnotherWindowChangesNothing()
    {
        var window = NewWindow();
        ToolWindowPlacement start = window.Placement;
        (float x, float y) = InTitle(start);
        ToolWindowInteractionResult result = window.Handle(new ToolWindowPointer(x, y, true, true), false, 0f, 2560, 1440);
        Check.True(!window.IsDragging && !window.IsBusy, "A press owned by a window above this one starts nothing.");
        Check.True(!result.PlacementChanged && !result.CloseRequested && !result.ResetRequested,
            "A press owned by another window decides nothing here.");
        Check.Equal(start, window.Placement, "The placement cannot change while another window owns the pointer.");
        // A gesture already in progress keeps the pointer even if the ownership answer changes mid-drag, which is
        // what stops a window from freezing under the cursor when it is dragged across another one.
        window.Handle(new ToolWindowPointer(x, y, true, true), true, 1f, 2560, 1440);
        window.Handle(new ToolWindowPointer(x + 50f, y, false, true), false, 1.1f, 2560, 1440);
        Check.Equal(start.X + 50f, window.Placement.X, "An active drag keeps following the pointer.");
    }

    internal static void TheCloseButtonClosesAndTheChromeOwnsItsOwnPresses()
    {
        var window = NewWindow();
        ToolWindowPlacement placement = window.Placement;
        ToolInputRect close = ToolWindowLayout.CloseBounds(placement.Width, placement.Height);
        // The chrome rectangles are window-local, so the test asks about the same point the host would.
        Check.True(window.ChromeHolds(placement.X + close.X + 2f, placement.Y + close.Y + 2f),
            "The close button is chrome and owns its press.");
        (float gx, float gy) = InGrip(placement);
        Check.True(window.ChromeHolds(gx, gy), "The resize grip is chrome even though it is drawn over the page.");
        (float tx, float ty) = InTitle(placement);
        Check.True(window.ChromeHolds(tx, ty), "The title band is chrome.");
        (float px, float py) = InPage(placement);
        Check.True(!window.ChromeHolds(px, py), "The page area belongs to the page, not to the chrome.");

        ToolWindowInteractionResult result = window.Handle(
            new ToolWindowPointer(placement.X + close.X + 2f, placement.Y + close.Y + 2f, true, true), true, 0f, 2560, 1440);
        Check.True(result.CloseRequested, "A press on the close button asks for a close.");
        Check.True(!window.IsBusy, "Closing is not a drag.");
        // A press in the page area is the page's: the chrome machine decides nothing about it.
        ToolWindowInteractionResult page = window.Handle(new ToolWindowPointer(px, py, true, true), true, .5f, 2560, 1440);
        Check.True(!page.CloseRequested && !page.ResetRequested && !window.IsBusy,
            "A press in the page area leaves the window alone.");
    }

    internal static void AWindowWithoutAGripLeavesThatCornerToThePage()
    {
        // A window whose page never asked for resizing must not keep an invisible grip: the rectangle would still be
        // honoured while nothing is drawn there, so a click near the bottom-right corner of the content would resize
        // the window instead of reaching the control the page drew under the pointer.
        var window = new ToolWindowInteraction(new ToolWindowPlacement(100f, 200f, 600f, 400f), resizable: false);
        ToolWindowPlacement start = window.Placement;
        (float x, float y) = InGrip(start);
        Check.True(!window.ChromeHolds(x, y), "An undrawn grip is not chrome.");
        ToolWindowInteractionResult press = window.Handle(new ToolWindowPointer(x, y, true, true), true, 0f, 2560, 1440);
        Check.True(!window.IsResizing, "A press in that corner does not start a resize.");
        Check.True(!press.CloseRequested && !press.ResetRequested && !press.PlacementChanged,
            "The corner press decides nothing for the window; it belongs to the page.");
        window.Handle(new ToolWindowPointer(x + 120f, y + 90f, false, true), true, .05f, 2560, 1440);
        Check.Equal(start, window.Placement, "The window cannot be resized from that corner while the grip is off.");
        // The size still follows the page, which is what makes the grip unnecessary rather than missing.
        Check.True(window.FollowContentSize(420f, 260f, 2560, 1440), "The page still decides the size.");
        Check.Equal(420f + ToolWindowLayout.Padding * 2, window.Placement.Width);
    }

    internal static void HidingTheWindowCancelsAnUnfinishedGesture()
    {
        var window = NewWindow();
        (float x, float y) = InTitle(window.Placement);
        window.Handle(new ToolWindowPointer(x, y, true, true), true, 0f, 2560, 1440);
        window.Handle(new ToolWindowPointer(x + 40f, y, false, true), true, .05f, 2560, 1440);
        Check.True(window.IsBusy, "The drag is running before the window is hidden.");
        ToolWindowPlacement moved = window.Placement;
        window.Cancel();
        Check.True(!window.IsBusy, "Hiding the window ends the gesture.");
        // The button that started the drag went away with the window, so the next frame's held button must not drag
        // the window to wherever the pointer now is.
        ToolWindowInteractionResult after = window.Handle(
            new ToolWindowPointer(x + 900f, y + 300f, false, true), true, .1f, 2560, 1440);
        Check.True(!after.PlacementChanged, "A cancelled gesture cannot move the window afterwards.");
        Check.Equal(moved, window.Placement, "The window stays where the cancelled gesture left it.");
    }

    internal static void TheScreenAndThePageBothKeepTheirSayOverTheSize()
    {
        // Following the page is how a two-view page gets the second view's height.
        var window = new ToolWindowInteraction(new ToolWindowPlacement(100f, 100f, 600f, 400f));
        Check.True(window.FollowContentSize(1200f, 300f, 2560, 1440), "A fresh window follows the page's size.");
        Check.Equal(1200f + ToolWindowLayout.Padding * 2, window.Placement.Width);
        Check.Equal(300f + ToolWindowLayout.Padding * 2 + ToolWindowLayout.HeaderRowHeight, window.Placement.Height);
        Check.True(!window.FollowContentSize(1200f, 300f, 2560, 1440), "An unchanged request changes nothing.");
        // A request larger than the screen is clamped, not honoured.
        window.FollowContentSize(9000f, 9000f, 1280, 720);
        Check.True(window.Placement.X + window.Placement.Width <= 1280f + .001f,
            "A page cannot ask for a window wider than the screen.");
        Check.True(window.Placement.Y + window.Placement.Height <= 720f + .001f,
            "A page cannot ask for a window taller than the screen.");
        // And a window placed on a larger screen is pulled back onto a smaller one.
        var placed = new ToolWindowInteraction(new ToolWindowPlacement(2200f, 1100f, 800f, 400f));
        Check.True(placed.KeepOnScreen(1280, 720), "A window placed off a smaller screen is pulled back into view.");
        Check.True(placed.Placement.X + placed.Placement.Width <= 1280f + .001f, "It stays inside the screen.");
        Check.True(!placed.KeepOnScreen(1280, 720), "A window already inside the screen is left alone.");
    }
}
