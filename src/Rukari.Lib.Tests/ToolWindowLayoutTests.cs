using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The standalone tool window's geometry: it is the one surface the user moves and resizes directly, so every rule
/// here is checked at the extremes rather than at one comfortable screen size.
/// </summary>
internal static class ToolWindowLayoutTests
{
    /// <summary>Every resolution the rules must hold at, including ones smaller than the default window.</summary>
    private static readonly (int Width, int Height)[] Screens =
    {
        (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3440, 1440), (3840, 2160), (1024, 640)
    };

    internal static void WindowChromeWrapsTheSizeThePageAskedFor()
    {
        // A page that asks for a content size gets exactly that coordinate space back: the chrome is added around
        // it, never taken out of it. This is what lets a page that already looks right inside the drawer be shown
        // in a window without a second layout.
        var (width, height) = ToolWindowLayout.WindowSize(2560, 1440, 500f, 560f);
        Check.Equal(500f + ToolWindowLayout.Padding * 2, width);
        Check.Equal(560f + ToolWindowLayout.Padding * 2 + ToolWindowLayout.HeaderRowHeight, height);
        ToolInputRect content = ToolWindowLayout.ContentBounds(width, height);
        Check.Equal(500f, content.Width);
        Check.Equal(560f, content.Height);
        Check.Equal(ToolWindowLayout.Padding, content.X);
        Check.Equal(ToolWindowLayout.Padding, content.Y);

        // A page with no opinion gets the shared default, and a page that asks for a size the screen cannot hold
        // gets the screen instead: a window larger than the display could never be dragged back into view.
        var (defaultWidth, _) = ToolWindowLayout.WindowSize(2560, 1440, 0f, 0f);
        Check.Equal(ToolWindowLayout.DefaultWidth + ToolWindowLayout.Padding * 2, defaultWidth);
        var (hugeWidth, hugeHeight) = ToolWindowLayout.WindowSize(1280, 720, 4000f, 4000f);
        Check.Equal(ToolWindowLayout.MaximumWidth(1280), hugeWidth);
        Check.Equal(ToolWindowLayout.MaximumHeight(720), hugeHeight);
        var (nanWidth, nanHeight) = ToolWindowLayout.WindowSize(1920, 1080, float.NaN, float.PositiveInfinity);
        Check.True(float.IsFinite(nanWidth) && nanWidth >= ToolWindowLayout.MinimumWidth,
            "A non-finite width request must fall back to the default size.");
        Check.True(float.IsFinite(nanHeight) && nanHeight >= ToolWindowLayout.MinimumHeight,
            "A non-finite height request must fall back to the default size.");
    }

    internal static void DefaultPlacementDocksBottomLeftAndClearsTheOfficialBar()
    {
        ToolWindowPlacement docked = ToolWindowLayout.DefaultPlacement(2560, 1440, 720f, 340f);
        Check.Equal(ToolWindowLayout.ScreenMargin, docked.X);
        Check.Equal(ToolWindowLayout.DockBottomMargin, docked.Y);
        // A very wide page still opens as a panel rather than as a full-width band: the workspace has to stay
        // usable beside it, and the user can drag the grip wider once it is on screen.
        ToolWindowPlacement wide = ToolWindowLayout.DefaultPlacement(2560, 1440, 2400f, 340f);
        Check.Near(2560 * ToolWindowLayout.DefaultWidthFraction, wide.Width,
            "A window opened for the first time must not cover the whole screen.");
        // The same cap applies to a page with no width opinion at all — including one whose measurement failed and
        // therefore reported zero — on a screen narrow enough for the default size to exceed it.
        ToolWindowPlacement quiet = ToolWindowLayout.DefaultPlacement(1024, 768, 0f, 0f);
        Check.True(quiet.Width <= 1024 * ToolWindowLayout.DefaultWidthFraction + .001f,
            "A page that asks for no width still opens inside the docked fraction of the screen.");
    }

    internal static void EveryScreenKeepsADockedWindowFullyVisible()
    {
        foreach ((int pixelWidth, int pixelHeight) in Screens)
        {
            foreach ((float preferredWidth, float preferredHeight) in new[] { (0f, 0f), (500f, 560f), (1400f, 300f) })
            {
                ToolWindowPlacement placement = ToolWindowLayout.DefaultPlacement(
                    pixelWidth, pixelHeight, preferredWidth, preferredHeight);
                string at = $"{pixelWidth}x{pixelHeight} asking {preferredWidth}x{preferredHeight}";
                Check.True(placement.IsValid, $"A default window must be drawable at {at}.");
                Check.True(placement.Width >= ToolWindowLayout.MinimumWidth
                    && placement.Width <= Math.Max(ToolWindowLayout.MinimumWidth, pixelWidth),
                    $"A default window must fit the width of {at}.");
                Check.True(placement.Height >= ToolWindowLayout.MinimumHeight
                    && placement.Height <= Math.Max(ToolWindowLayout.MinimumHeight, pixelHeight),
                    $"A default window must fit the height of {at}.");
                Check.True(placement.X >= 0f && placement.X + placement.Width <= pixelWidth + .001f,
                    $"A default window must stay inside the screen horizontally at {at}.");
                Check.True(placement.Y >= 0f && placement.Y + placement.Height <= pixelHeight + .001f,
                    $"A default window must stay inside the screen vertically at {at}.");
                // The content rectangle is where the page draws and where it is hit-tested, so it has to be inside
                // the window at every resolution, not only on a large one.
                ToolInputRect content = ToolWindowLayout.ContentPixels(placement);
                Check.True(content.X >= placement.X && content.Y >= placement.Y
                    && content.X + content.Width <= placement.X + placement.Width + .001f
                    && content.Y + content.Height <= placement.Y + placement.Height + .001f,
                    $"The content rectangle must stay inside the window at {at}.");
            }
        }
    }

    internal static void ResizingGrowsFromTheBottomRightCornerAndStopsAtTheLimits()
    {
        ToolWindowPlacement start = ToolWindowLayout.DefaultPlacement(2560, 1440, 720f, 340f);
        ToolWindowPlacement grown = ToolWindowLayout.Resize(start, 120f, 60f, 2560, 1440);
        Check.Equal(start.X, grown.X);
        Check.Equal(start.Y, grown.Y);
        Check.Equal(start.Width + 120f, grown.Width);
        Check.Equal(start.Height + 60f, grown.Height);

        // Dragging the grip past the minimum stops at the minimum instead of inverting the window.
        ToolWindowPlacement small = ToolWindowLayout.Resize(start, -10000f, -10000f, 2560, 1440);
        Check.Equal(ToolWindowLayout.MinimumWidth, small.Width);
        Check.Equal(ToolWindowLayout.MinimumHeight, small.Height);

        // And past the screen stops at the screen: the bottom-left corner is the anchor, so the window grows until
        // its far corner reaches the display edge and no further.
        ToolWindowPlacement big = ToolWindowLayout.Resize(start, 10000f, 10000f, 1920, 1080);
        Check.True(big.X + big.Width <= 1920f + .001f, "A resized window must not cross the right screen edge.");
        Check.True(big.Y + big.Height <= 1080f + .001f, "A resized window must not cross the top screen edge.");
        Check.Near(ToolWindowLayout.MaximumWidth(1920), big.Width,
            "A resized window must stop where the screen does.");
    }

    internal static void AStoredPositionIsPulledBackOntoASmallerScreen()
    {
        var store = new ToolWindowPlacementStore();
        Check.True(!store.TryGet("timeline", 2560, 1440, out _), "An unplaced window must report no stored position.");
        store.Remember("timeline", new ToolWindowPlacement(1700f, 900f, 800f, 400f));
        Check.True(store.TryGet("timeline", 2560, 1440, out ToolWindowPlacement remembered),
            "A window the user moved must come back where it was left.");
        Check.Equal(1700f, remembered.X);
        Check.Equal(900f, remembered.Y);
        // An entry that no longer fits its screen is clamped on the way out rather than trusted: a position is
        // only ever as valid as the display it is re-read on.
        store.Remember("edge", new ToolWindowPlacement(2500f, 1300f, 900f, 500f));
        Check.True(store.TryGet("edge", 2560, 1440, out ToolWindowPlacement edge), "A stored entry must be readable.");
        Check.Equal(2560f - 900f, edge.X);
        Check.Equal(1440f - 500f, edge.Y);
        // The same entry on a smaller display: the window is pulled fully into view rather than drawn off screen,
        // which is the difference between "my window moved" and "my window is gone".
        Check.True(store.TryGet("timeline", 1280, 720, out ToolWindowPlacement pulled), "A stored entry must survive a resize.");
        Check.True(pulled.X >= 0f && pulled.X + pulled.Width <= 1280f + .001f,
            "A window stored on a large screen must be pulled back onto a smaller one.");
        Check.True(pulled.Y >= 0f && pulled.Y + pulled.Height <= 720f + .001f,
            "A window stored on a large screen must be pulled back onto a smaller one.");
        // Resetting is the gesture's whole contract: it reports whether there was anything to forget, and the
        // window then opens at its default place again.
        Check.True(store.Reset("timeline"), "Resetting a placed window must report the change.");
        Check.True(!store.Reset("timeline"), "Resetting an unplaced window must not report a change.");
        Check.True(!store.TryGet("timeline", 2560, 1440, out _), "A reset window must open at its default place.");
        Check.Equal(1, store.Count);
        store.Clear();
        Check.Equal(0, store.Count);
    }

    internal static void ChromeStaysInsideTheWindowAndNeverOverlapsItself()
    {
        foreach ((float width, float height) in new[]
        {
            (ToolWindowLayout.MinimumWidth, ToolWindowLayout.MinimumHeight),
            (720f + ToolWindowLayout.Padding * 2, 340f + ToolWindowLayout.Padding * 2 + ToolWindowLayout.HeaderRowHeight),
            (1400f, 300f)
        })
        {
            ToolInputRect title = ToolWindowLayout.TitleBounds(width, height);
            ToolInputRect close = ToolWindowLayout.CloseBounds(width, height);
            ToolInputRect grip = ToolWindowLayout.GripBounds(width, height);
            ToolInputRect content = ToolWindowLayout.ContentBounds(width, height);
            string at = $"{width}x{height}";
            foreach ((string name, ToolInputRect rect) in new[] { ("title", title), ("close", close), ("grip", grip), ("content", content) })
            {
                Check.True(rect.X >= 0f && rect.Y >= 0f && rect.X + rect.Width <= width + .001f
                    && rect.Y + rect.Height <= height + .001f,
                    $"The {name} rectangle must stay inside a {at} window with no negative coordinates.");
            }
            Check.True(title.X + title.Width <= close.X + .001f,
                $"The title band must stop at the close button in a {at} window, so a press is never ambiguous.");
            Check.True(content.Y + content.Height <= title.Y + .001f,
                $"The content must start above the title band in a {at} window.");
            Check.True(close.Y >= title.Y - .001f && close.Y + close.Height <= title.Y + title.Height + .001f,
                $"The close button must sit inside the header row in a {at} window.");
        }
    }

    internal static void OnlyTheTopmostWindowOwnsAPressInAnOverlap()
    {
        var stack = new ToolWindowStack();
        Check.Equal(-1, stack.Depth("a"), "A window that is not on screen has no depth.");
        stack.Show("a", new ToolWindowPlacement(100f, 100f, 400f, 300f));
        stack.Show("b", new ToolWindowPlacement(300f, 200f, 400f, 300f));
        Check.Equal(2, stack.Count);
        Check.Equal(0, stack.Depth("a"), "The first window shown is at the bottom.");
        Check.Equal(1, stack.Depth("b"), "A window that appears later arrives on top.");
        // The overlap belongs to the window on top. The input guard is a union of rectangles and cannot say this,
        // which is the whole reason the hosts share this table.
        Check.Equal("b", stack.OwnerAt(400f, 300f));
        Check.Equal("a", stack.OwnerAt(150f, 150f));
        Check.True(stack.OwnerAt(150f, 600f) is null, "A point over no window belongs to nobody.");
        // A press on the lower window brings it forward, and the same point then belongs to it.
        stack.Raise("a");
        Check.Equal(1, stack.Depth("a"));
        Check.Equal(0, stack.Depth("b"));
        Check.Equal("a", stack.OwnerAt(400f, 300f));
        // Moving a window changes what it owns without touching the order.
        stack.Update("b", new ToolWindowPlacement(1000f, 100f, 200f, 200f));
        Check.Equal(0, stack.Depth("b"), "Moving a window must not reorder it.");
        Check.Equal("a", stack.OwnerAt(400f, 300f));
        Check.Equal("b", stack.OwnerAt(1050f, 150f));
        // Hiding removes it from the order and from hit testing.
        stack.Hide("a");
        Check.Equal(1, stack.Count);
        Check.Equal(-1, stack.Depth("a"));
        Check.True(stack.OwnerAt(400f, 300f) is null, "A hidden window owns nothing.");
        // Showing it again puts it back on top, the way a freshly opened window appears.
        stack.Show("a", new ToolWindowPlacement(100f, 100f, 400f, 300f));
        Check.Equal(1, stack.Depth("a"));
        // Unknown and empty ids are ignored rather than throwing: a window can be released mid-frame.
        stack.Raise("missing");
        stack.Hide("missing");
        stack.Show("", default);
        Check.Equal(2, stack.Count);
        stack.Clear();
        Check.Equal(0, stack.Count);
        Check.True(stack.OwnerAt(100f, 100f) is null, "A cleared stack owns nothing.");
    }

    internal static void TheFrameConversionsAreInversesAndKeepThePaddingOnce()
    {
        var placement = new ToolWindowPlacement(300f, 200f, 700f, 500f);
        // A page coordinate and the screen point that aims at it are the same point seen from two sides.
        (float sx, float sy) = ToolWindowFrame.ScreenPoint(placement, 120f, 40f);
        Check.Equal(300f + ToolWindowLayout.Padding + 120f, sx);
        Check.Equal(200f + ToolWindowLayout.Padding + 40f, sy);
        (float cx, float cy) = ToolWindowFrame.ContentPointer(placement, sx, sy);
        Check.Near(120d, cx, "The screen point of a page coordinate maps back to it.");
        Check.Near(40d, cy, "The screen point of a page coordinate maps back to it.");

        // The content rectangle's own origin is the page's (0,0), which is what makes the padding apply exactly once.
        ToolInputRect content = ToolWindowLayout.ContentBounds(placement.Width, placement.Height);
        (float originX, float originY) = ToolWindowFrame.ScreenPoint(placement, 0f, 0f);
        Check.Equal(placement.X + content.X, originX);
        Check.Equal(placement.Y + content.Y, originY);
        (float backX, float backY) = ToolWindowFrame.ContentPointer(placement, originX, originY);
        Check.Equal(0f, backX);
        Check.Equal(0f, backY);

        // A control lands inside the window moved by the padding, and keeps its size.
        ToolInputRect element = ToolWindowFrame.ElementBounds(new ToolInputRect(10f, 20f, 60f, 30f));
        Check.Equal(10f + ToolWindowLayout.Padding, element.X);
        Check.Equal(20f + ToolWindowLayout.Padding, element.Y);
        Check.Equal(60f, element.Width);
        Check.Equal(30f, element.Height);
        Check.True(element.X >= 0f && element.Y >= 0f
            && element.X + element.Width <= placement.Width + .001f
            && element.Y + element.Height <= placement.Height + .001f,
            "A control the page drew inside its content lands inside the window.");

        // And a window-local rectangle becomes a screen rectangle, which is what a hit test compares against.
        ToolInputRect screen = ToolWindowFrame.ToScreenPixels(placement, element);
        Check.Equal(placement.X + element.X, screen.X);
        Check.Equal(placement.Y + element.Y, screen.Y);
        Check.Equal(element.Width, screen.Width);
    }

    internal static void AWindowKnowsWhetherAPointIsInsideIt()
    {
        var placement = new ToolWindowPlacement(100f, 200f, 400f, 300f);
        Check.True(ToolWindowLayout.Contains(placement, 100f, 200f), "The bottom-left corner belongs to the window.");
        Check.True(ToolWindowLayout.Contains(placement, 499f, 499f), "The far corner belongs to the window.");
        Check.True(!ToolWindowLayout.Contains(placement, 99f, 300f), "A point left of the window belongs to nobody.");
        Check.True(!ToolWindowLayout.Contains(placement, 300f, 501f), "A point above the window belongs to nobody.");
        Check.True(!ToolWindowLayout.Contains(default, 0f, 0f), "An undrawn window owns no rectangle.");
    }
}
