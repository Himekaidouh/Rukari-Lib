using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The module column is a card: a titled plate that holds the module rows and scrolls when they no longer fit.
/// Two rules are pinned here because they are the whole point of the shape:
///
/// * one row is always <see cref="ToolDrawerLayout.ModuleButtonHeight"/> tall. The column used to shrink the whole
///   drawer by the module count, so every mod that registered itself made all the others smaller;
/// * the card shows exactly <see cref="ToolDrawerLayout.ModuleCardRowLimit"/> rows — five — on every screen, and
///   the module after the fifth is reached by scrolling. A column that grew with the window was a different shape
///   on every machine, and one that filled a large window's left edge dwarfed the icon beside it.
/// </summary>
internal static class ToolModuleCardTests
{
    internal static void TheCardScrollsInsteadOfShrinkingTheRail()
    {
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");
        foreach (var size in new[] { (3840, 2160), (2560, 1440), (1920, 1080), (1280, 720), (1024, 576) })
        {
            ToolDrawerPlacement few = layout.PlaceTree(size.Item1, size.Item2, FourModules,
                0, Array.Empty<ToolEntry>(), -1);
            ToolDrawerPlacement many = layout.PlaceTree(size.Item1, size.Item2, CrowdedModules,
                0, Array.Empty<ToolEntry>(), -1);
            float scale = ToolDrawerLayout.RailScaleFor(size.Item1, size.Item2);

            // The rail follows the window and nothing else. It is the same number for four mods and for forty,
            // which is what stops a new mod from shrinking every icon beside it.
            Check.True(scale > 0 && float.IsFinite(scale), "The rail scale must be a usable number.");
            Check.Equal(ToolDrawerLayout.RailButtonHeight, many.Master.Height,
                "The big icon keeps its size however many mods are installed.");
            Check.Equal(
                Math.Clamp(size.Item2 / 900f, ToolDrawerLayout.MinimumRailScale, ToolDrawerLayout.MaximumRailScale),
                scale, "The rail scale follows the window height and nothing else, not the module count.");

            // The card is the same shape on every screen: five rows, however tall the window is and however many
            // mods are installed. The sixth module is what makes the scrollbar appear.
            Check.Equal(ToolDrawerLayout.ModuleCardRowLimit, many.Children.Count,
                $"The card shows exactly its five rows; screen {size}.");
            Check.Equal(ToolDrawerLayout.ModuleCardHeight(ToolDrawerLayout.ModuleCardRowLimit), many.Card.Height,
                "The card is the same height on every screen.");
            Check.True(many.Children.Count < CrowdedModules.Length,
                "This case only means something while the list does not fit.");
            foreach (ToolInputRect row in many.Children)
            {
                Check.Equal(ToolDrawerLayout.ModuleButtonHeight, row.Height,
                    "A row is always a full-height official plate, never a shrunken one.");
                Check.Equal(few.Children[0].Height, row.Height,
                    "Four mods and forty mods draw rows of the very same height.");
                Check.True(row.Y >= 0 && row.Y + row.Height <= size.Item2 + .01,
                    $"Every row stays on screen; row {row}, screen {size}.");
                Check.True(row.X >= many.Card.X - .01 && row.X + row.Width <= many.Card.X + many.Card.Width + .01,
                    $"Every row stays inside its card; row {row}, card {many.Card}.");
            }

            // The card itself keeps clear of both screen corners, so the guard band it publishes is inside the
            // screen and the official controls near the corners stay clickable.
            Check.True(many.Card.Y >= ToolDrawerLayout.MasterScreenMargin - .01,
                $"The card keeps its margin from the bottom edge; card {many.Card}, screen {size}.");
            Check.True(many.Card.Y + many.Card.Height <= size.Item2 - ToolDrawerLayout.MasterScreenMargin + .01,
                $"The card keeps its margin from the top edge; card {many.Card}, screen {size}.");
            Check.True(many.CatchAll.Y >= 0 && many.CatchAll.Y + many.CatchAll.Height <= size.Item2 + .01,
                "A full card still publishes a guard band that fits the screen.");
            Check.True(many.CatchAll.X + many.CatchAll.Width < many.Panel.X + .01,
                "The guard band still never reaches into the panel.");

            // The scrollbar's gutter is part of the card whether or not the scrollbar is showing, so registering
            // one more mod cannot move the panel under the pointer.
            Check.Equal(few.Panel.X, many.Panel.X,
                $"The panel must not move when the module list grows; screen {size}.");
            Check.Equal(few.Card.Width, many.Card.Width,
                "The card is as wide for forty mods as it is for four: the gutter is reserved.");
        }
    }

    internal static void TheCardShowsEveryRowWhileTheyFit()
    {
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");
        foreach (var size in new[] { (2560, 1440), (1920, 1080), (1280, 720) })
        {
            ToolDrawerPlacement placement = layout.PlaceTree(size.Item1, size.Item2, FourModules,
                0, Array.Empty<ToolEntry>(), -1);
            Check.Equal(0, placement.FirstVisibleRow, "A list that fits starts at its first row.");
            Check.Equal(FourModules.Length, placement.Children.Count, "Every registered mod gets a row.");
            Check.Equal(FourModules.Length, placement.VisibleModules.Count,
                "Every row knows which mod it opens.");
            for (int i = 0; i < placement.VisibleModules.Count; i++)
                Check.Equal(i, placement.VisibleModules[i], "A list that fits is shown in registration order.");
            Check.True(!placement.ScrollTrack.IsValid && !placement.ScrollThumb.IsValid,
                "No scrollbar is drawn while every module fits.");

            // A scrollbar that appears only when it is needed must not have been reserving the row width either:
            // the rows are as wide as the longest name needs in both cases.
            ToolDrawerPlacement many = layout.PlaceTree(size.Item1, size.Item2, CrowdedModules,
                0, Array.Empty<ToolEntry>(), -1);
            Check.Equal(placement.Children[0].Width, many.Children[0].Width,
                "A row is as wide as its own label needs, with or without a scrollbar.");
            Check.True(many.ScrollTrack.IsValid && many.ScrollThumb.IsValid,
                "A list that does not fit gets a track and a thumb.");
            Check.True(many.ScrollThumb.Height >= ToolDrawerLayout.ModuleScrollbarMinimumThumb - .01,
                "The thumb stays big enough to grab even for a very long list.");
            Check.True(many.ScrollThumb.Y >= many.ScrollTrack.Y - .01
                && many.ScrollThumb.Y + many.ScrollThumb.Height <= many.ScrollTrack.Y + many.ScrollTrack.Height + .01,
                $"The thumb stays inside its track; thumb {many.ScrollThumb}, track {many.ScrollTrack}.");
            Check.True(many.ScrollTrack.X + many.ScrollTrack.Width <= many.Card.X + many.Card.Width + .01,
                "The scrollbar lives inside the card's own right inset.");
        }
    }

    /// <summary>
    /// The exact rule the drawer is designed around: five modules fit, and the sixth is what makes the scrollbar
    /// appear. Nothing here depends on the window: the same five rows are shown on a laptop and on a 4K screen.
    /// </summary>
    internal static void TheSixthModuleStartsTheScrolling()
    {
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");
        ToolModule[] Five = CrowdedModules.Take(ToolDrawerLayout.ModuleCardRowLimit).ToArray();
        ToolModule[] Six = CrowdedModules.Take(ToolDrawerLayout.ModuleCardRowLimit + 1).ToArray();
        foreach (var size in new[] { (3840, 2160), (2560, 1440), (1920, 1080), (1366, 768), (1280, 720) })
        {
            ToolDrawerPlacement five = layout.PlaceTree(size.Item1, size.Item2, Five, 0, Array.Empty<ToolEntry>(), -1);
            ToolDrawerPlacement six = layout.PlaceTree(size.Item1, size.Item2, Six, 0, Array.Empty<ToolEntry>(), -1);
            Check.Equal(ToolDrawerLayout.ModuleCardRowLimit, five.Children.Count,
                $"Five modules all get a row; screen {size}.");
            Check.True(!five.ScrollTrack.IsValid,
                $"Five modules must not scroll, on any screen; screen {size}.");
            Check.Equal(ToolDrawerLayout.ModuleCardRowLimit, six.Children.Count,
                $"The sixth module scrolls instead of making the card taller; screen {size}.");
            Check.Equal(five.Card.Height, six.Card.Height,
                $"The card is the same height with five modules and with six; screen {size}.");
            Check.Equal(five.Card.Y, six.Card.Y, "…and it does not move either.");
            Check.True(six.ScrollTrack.IsValid && six.ScrollThumb.IsValid,
                $"The sixth module gets a scrollbar; screen {size}.");
            Check.Equal(1, ToolScroll.ClampFirst(99, Six.Length, six.Children.Count),
                "One more module than the card holds is exactly one row of scrolling.");
        }
        Check.Equal(5, ToolDrawerLayout.ModuleCardRowLimit,
            "The card is designed around five rows; changing that is a design decision, not a tweak.");

        // A window too short for the five-row card gives up rows rather than covering the corners.
        Check.Equal(ToolDrawerLayout.ModuleCardRowLimit, ToolDrawerLayout.ModuleRowsThatFitOnScreen(720));
        Check.True(ToolDrawerLayout.ModuleRowsThatFitOnScreen(300) < ToolDrawerLayout.ModuleCardRowLimit,
            "An absurdly short window shows fewer rows instead of overflowing it.");
        Check.True(ToolDrawerLayout.VisibleModuleRows(300, 40) <= ToolDrawerLayout.ModuleRowsThatFitOnScreen(300),
            "The rows shown never exceed what the window can hold.");
    }

    internal static void EveryModuleStaysReachableThroughTheScrollbar()
    {
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");
        ToolDrawerPlacement top = layout.PlaceTree(1920, 1080, FortyModules, 0, Array.Empty<ToolEntry>(), -1);
        int visible = top.Children.Count;
        int scrollable = FortyModules.Length - visible;
        Check.True(scrollable > 0, "This case only means something while the list does not fit.");

        // Walking the scroll position through every legal value reaches every module exactly once, so no mod can
        // be hidden behind the scrollbar.
        var reached = new HashSet<int>();
        for (int first = -5; first <= scrollable + 5; first++)
        {
            ToolDrawerPlacement placement = layout.PlaceTree(1920, 1080, FortyModules, 0,
                Array.Empty<ToolEntry>(), -1, treeOpen: true, firstRow: first);
            Check.True(placement.FirstVisibleRow >= 0 && placement.FirstVisibleRow <= scrollable,
                $"A scroll position is clamped into the list; asked {first}, got {placement.FirstVisibleRow}.");
            Check.Equal(placement.FirstVisibleRow, placement.VisibleModules[0],
                "The first drawn row is the module the placement says it starts at.");
            Check.Equal(visible, placement.Children.Count,
                "A scrolled card still draws a full page of rows.");
            Check.Equal(scrollable, ToolScroll.ClampFirst(int.MaxValue, FortyModules.Length, visible),
                "Scrolling to the end stops at the last full page.");
            foreach (int module in placement.VisibleModules) reached.Add(module);
        }
        Check.Equal(FortyModules.Length, reached.Count, "Every registered mod is reachable by scrolling.");

        // The thumb and the drag are inverses: dragging the thumb to where a row's own thumb was lands on that
        // row, which is what makes the bar usable instead of approximate.
        ToolInputRect track = top.ScrollTrack;
        for (int first = 0; first <= scrollable; first++)
        {
            ToolInputRect thumb = ToolScroll.Thumb(track, first, FortyModules.Length, visible,
                ToolDrawerLayout.ModuleScrollbarMinimumThumb);
            Check.Equal(first,
                ToolScroll.RowForThumbTop(track, thumb.Height, thumb.Y, FortyModules.Length, visible),
                "Dragging the thumb to a position shows the row that position belongs to.");
            Check.Equal(first,
                ToolScroll.RowForTrackPoint(track, thumb.Height, thumb.Y + thumb.Height / 2f,
                    FortyModules.Length, visible),
                "Clicking the middle of the thumb never jumps away from it.");
        }
        Check.Equal(0, ToolScroll.RowForThumbTop(track, top.ScrollThumb.Height, track.Y,
            FortyModules.Length, visible), "The thumb at the bottom of the track shows the first row.");
        Check.Equal(scrollable, ToolScroll.RowForThumbTop(track, top.ScrollThumb.Height,
            track.Y + track.Height, FortyModules.Length, visible),
            "The thumb at the top of the track shows the last page.");
    }

    internal static void ToolScrollKeepsTheWheelAndTheThumbInAgreement()
    {
        Check.Equal(0, ToolScroll.ClampFirst(0, 4, 4), "A list that fits never scrolls.");
        Check.Equal(0, ToolScroll.ClampFirst(3, 4, 4), "A list that fits never scrolls, even when asked to.");
        Check.Equal(0, ToolScroll.ClampFirst(-9, 40, 10), "Scrolling above the first row clamps.");
        Check.Equal(30, ToolScroll.ClampFirst(99, 40, 10), "Scrolling past the end clamps to the last full page.");
        Check.Equal(5, ToolScroll.ClampFirst(5, 40, 10), "A legal scroll position is kept exactly.");
        Check.Equal(0, ToolScroll.ClampFirst(5, 4, 10), "An empty or short list can never scroll.");
        Check.True(!ToolScroll.Needed(4, 4), "A list that exactly fills the card needs no scrollbar.");
        Check.True(!ToolScroll.Needed(8, 0), "A card with no visible rows cannot scroll at all.");
        Check.True(ToolScroll.Needed(9, 4), "One row too many is exactly when the scrollbar appears.");
        Check.Equal(-1, ToolScroll.WheelRows(1f), "A wheel notch away from the user scrolls the list up.");
        Check.Equal(1, ToolScroll.WheelRows(-1f), "A wheel notch towards the user scrolls the list down.");
        Check.Equal(0, ToolScroll.WheelRows(0f), "A resting wheel moves nothing.");
        Check.True(!ToolScroll.Thumb(default, 0, 40, 10, 36).IsValid,
            "A card with no scrollbar has no thumb to draw.");

        // A longer list gets a shorter thumb, but never one that cannot be grabbed.
        var track = new ToolInputRect(100, 200, 8, 300);
        Check.True(ToolScroll.Thumb(track, 0, 40, 10, 36).Height
                < ToolScroll.Thumb(track, 0, 20, 10, 36).Height,
            "A shorter list gets a longer thumb.");
        Check.Equal(36f, ToolScroll.Thumb(track, 0, 4000, 10, 36).Height,
            "The thumb stops shrinking at the minimum, so it stays grabbable.");
        Check.True(ToolScroll.Thumb(track, 0, 11, 10, 36).Height
                > ToolScroll.Thumb(track, 0, 20, 10, 36).Height,
            "A list one row too long gets a nearly full-height thumb.");
    }

    /// <summary>
    /// The card header is the column's own title, with the control that collapses it. Both have to stay inside the
    /// card, above every row, and the collapse control must not sit on top of the title.
    /// </summary>
    internal static void TheCardHeaderCarriesItsTitleAndCollapseControl()
    {
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");
        foreach (var modules in new[] { OneModule, FourModules, FortyModules })
        foreach (var size in new[] { (2560, 1440), (1920, 1080), (1280, 720) })
        {
            ToolDrawerPlacement placement = layout.PlaceTree(size.Item1, size.Item2, modules, 0,
                Array.Empty<ToolEntry>(), -1);
            ToolInputRect card = placement.Card;
            ToolInputRect header = placement.CardHeader;
            ToolInputRect collapse = placement.CardCollapse;
            Check.True(card.IsValid, "A registered module draws a card.");
            Check.True(header.IsValid && collapse.IsValid, "The card always has a header and a collapse control.");
            Check.Equal(ToolDrawerLayout.ModuleCardHeaderHeight, header.Height, "The header keeps its own height.");
            Check.Equal(card.Y + card.Height, header.Y + header.Height, "The header is the top of the card.");
            Check.True(header.X >= card.X - .01 && header.X + header.Width <= card.X + card.Width + .01,
                $"The header stays inside the card; header {header}, card {card}.");
            Check.True(collapse.X >= header.X - .01 && collapse.X + collapse.Width <= header.X + header.Width + .01,
                $"The collapse control stays inside the header; collapse {collapse}, header {header}.");
            Check.True(collapse.Y >= header.Y - .01 && collapse.Y + collapse.Height <= header.Y + header.Height + .01,
                "The collapse control stays inside the header vertically too.");
            Check.True(collapse.Width >= 24 && collapse.Height >= 24,
                "The collapse control must stay big enough to hit.");
            foreach (ToolInputRect row in placement.Children)
                Check.True(!Overlaps(row, header),
                    $"A row must never reach into the title band; row {row}, header {header}.");
        }

        // A drawer with nothing registered draws no card at all, and still places its one permanent icon.
        ToolDrawerPlacement empty = layout.PlaceTree(1920, 1080, Array.Empty<ToolModule>(),
            0, Array.Empty<ToolEntry>(), -1);
        Check.True(!empty.Card.IsValid, "No modules means no card.");
        Check.Equal(0, empty.Children.Count, "No modules means no rows.");
        Check.True(empty.Master.IsValid, "The big icon is permanent even with no modules at all.");
    }

    private static bool Overlaps(ToolInputRect a, ToolInputRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static readonly ToolModule[] OneModule = { new("rukari.charactervoice", "人物配音支持", "wave") };

    private static readonly ToolModule[] FourModules =
    {
        new("rukari.charactervoice", "人物配音支持", "wave"),
        new("rukari.moreeffects", "更多的画面效果", "camera"),
        new("rukari.memorylobby", "记忆大厅支持", "card"),
        new("third.party.mod", "第三方模组", "")
    };

    /// <summary>A library large enough that the card cannot show it all on any of the screens tested.</summary>
    private static readonly ToolModule[] FortyModules = Enumerable.Range(0, 40)
        .Select(i => new ToolModule("mod" + i, "模组" + i, "")).ToArray();

    /// <summary>
    /// The shipped modules plus enough more to overflow the card, with the SAME longest name as
    /// <see cref="FourModules"/>. Two lists whose longest name differs are two different column widths, so only
    /// this pair isolates what the scrollbar itself does to the layout.
    /// </summary>
    private static readonly ToolModule[] CrowdedModules = FourModules
        .Concat(Enumerable.Range(0, 36).Select(i => new ToolModule("extra" + i, "模组" + i, "")))
        .ToArray();
}
