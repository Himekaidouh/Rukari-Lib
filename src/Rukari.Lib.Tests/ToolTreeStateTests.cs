using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The rail is a three-level tree with at most one open entry per level. These rules decide whether a mod can
/// cover another mod's panel, so they are pinned here in the pure-managed layer rather than in the renderer.
/// </summary>
internal static class ToolTreeStateTests
{
    private static readonly ToolModule[] TwoModules =
    {
        new("rukari.a", "A", "wave"),
        new("rukari.b", "B", "camera")
    };

    /// <summary>
    /// The big icon opens the module column and selects NOTHING. A column that came up with a module already open
    /// both lit a button the user never pressed and turned the next press on that button into a close, so the
    /// button appeared to do nothing at all — which is exactly what the user reported.
    /// </summary>
    public static void TheRailIconOpensTheColumnAndSelectsNothing()
    {
        var tree = new ToolTreeState();
        Check.Equal(0, tree.Depth);
        Check.True(!tree.IsExpanded, "The rail must start collapsed.");

        Check.True(tree.PressRailIcon(), "The first press opens the module column.");
        Check.Equal(1, tree.Depth, "The module column is one level, and no panel is showing.");
        Check.True(tree.IsExpanded, "The column is out of the rail.");
        Check.True(tree.OpenModuleId == null, "The rail icon must not select a module on the user's behalf.");
        Check.True(tree.OpenEntryId == null, "…and must not open a panel either.");

        Check.True(tree.PressRailIcon(), "The second press closes every level at once.");
        Check.Equal(0, tree.Depth);
        Check.True(!tree.IsExpanded, "A closed rail keeps no column out.");

        // A module the user actually presses is the only thing that can open one.
        tree.PressRailIcon();
        Check.True(tree.OpenModule("rukari.a"), "Pressing a module button opens that module.");
        Check.Equal(1, tree.Depth, "An open module with no panel is still one level.");
        Check.Equal("rukari.a", tree.OpenModuleId);
        Check.True(tree.OpenEntryId == null, "Opening a module must not invent an open entry.");
    }

    /// <summary>
    /// One press, one visible level. A single-page module draws no entries column, so its button opens its panel
    /// directly and the panel and the module close together; a multi-entry module keeps its column, because that
    /// column is a level the user can see.
    /// </summary>
    public static void OnePressIsOneVisibleLevel()
    {
        var single = new ToolTreeState();
        single.OpenEntry("rukari.a", "stage");
        Check.Equal(2, single.Depth);
        Check.Equal(1, single.CollapseOneLevel(moduleHasEntries: false),
            "A single-page module has no entries level, so its panel and its module go together.");
        Check.True(single.OpenModuleId == null, "Nothing stays highlighted over an empty column.");
        Check.True(single.IsExpanded, "The column the user opened stays out.");
        Check.Equal(0, single.CollapseOneLevel(false), "The next press puts the column away.");

        var multi = new ToolTreeState();
        multi.OpenEntry("rukari.a", "stage");
        Check.Equal(1, multi.CollapseOneLevel(moduleHasEntries: true),
            "A multi-entry module keeps its entries column when its panel closes.");
        Check.Equal("rukari.a", multi.OpenModuleId, "The user keeps their place in the module.");
        Check.True(multi.OpenEntryId == null, "No panel is open any more.");
        Check.Equal(1, multi.CollapseOneLevel(true), "The next press closes the module and keeps the column.");
        Check.True(multi.OpenModuleId == null, "No module stays open over the column.");
        Check.True(multi.IsExpanded, "The column is still out.");
        Check.Equal(0, multi.CollapseOneLevel(true), "The last press closes the column.");

        // Closing the module directly is the same one-level step, and it is never a no-op on an open module.
        var direct = new ToolTreeState();
        direct.OpenEntry("rukari.a", "stage");
        Check.True(direct.CloseModule(), "Closing an open module reports a change.");
        Check.True(direct.OpenModuleId == null && direct.OpenEntryId == null, "Both the panel and the module are gone.");
        Check.True(direct.IsExpanded, "Closing the module leaves the column standing.");
        Check.True(!direct.CloseModule(), "Closing a module that is not open changes nothing.");
    }

    public static void OnlyOneModuleIsOpenAtATime()
    {
        var tree = new ToolTreeState();
        tree.OpenEntry("rukari.a", "stage");

        Check.True(tree.OpenModule("rukari.b"), "Another module can be opened.");
        Check.Equal("rukari.b", tree.OpenModuleId, "Opening a module closes the previous one.");
        Check.True(tree.OpenEntryId == null,
            "Switching module must drop the previous module's entry: an entry id only means something inside "
            + "the module that published it.");
        Check.Equal(1, tree.Depth);
        Check.True(!tree.OpenModule("rukari.b"), "Opening the module that is already open is not a change.");
    }

    /// <summary>
    /// "No module is open" has to reach the drawing as -1. Clamping it to the first module lit a button the user
    /// never pressed, and the next press on that button then read as a close, which is the same dead press the
    /// rail icon was fixed for — this time in the renderer rather than in the state machine.
    /// </summary>
    public static void AnOpenColumnWithNoModuleLightsNoButton()
    {
        Check.Equal(-1, ToolDrawerLayout.HighlightedModuleIndex(-1, 3),
            "With nothing open, no module button may look pressed.");
        Check.Equal(-1, ToolDrawerLayout.HighlightedModuleIndex(0, 0),
            "An empty rail has no button to light.");
        Check.Equal(-1, ToolDrawerLayout.HighlightedModuleIndex(5, 3),
            "An index that is not on the rail lights nothing.");
        Check.Equal(0, ToolDrawerLayout.HighlightedModuleIndex(0, 3), "The first module can be the open one.");
        Check.Equal(2, ToolDrawerLayout.HighlightedModuleIndex(2, 3), "The last module can be the open one.");

        // The shipped configuration exercises the real entry point: the icon opens the column, and the state that
        // comes out of it must light nothing.
        var tree = new ToolTreeState();
        tree.PressRailIcon();
        Check.Equal(-1, ToolDrawerLayout.HighlightedModuleIndex(-1, 2),
            "A column opened by the rail icon has no open module to highlight.");
        tree.OpenEntry("rukari.b", "voice");
        Check.Equal(1, ToolDrawerLayout.HighlightedModuleIndex(1, 2), "Opening a module lights exactly that one.");
    }

    public static void OnlyOneEntryIsOpenAtATime()
    {
        var tree = new ToolTreeState();
        tree.OpenEntry("rukari.a", "stage");
        Check.Equal(2, tree.Depth);
        Check.Equal("stage", tree.OpenEntryId);

        tree.OpenEntry("rukari.a", "camera");
        Check.Equal("camera", tree.OpenEntryId, "A second entry replaces the first.");

        tree.OpenEntry("rukari.b", "voice");
        Check.Equal("rukari.b", tree.OpenModuleId, "Opening an entry directly fills in the module level.");
        Check.Equal("voice", tree.OpenEntryId);
        Check.Equal(2, tree.Depth);
    }

    /// <summary>
    /// The close button walks back one VISIBLE level and leaves the column the user opened standing: panel,
    /// then the module, then the column. Collapsing straight to the rail would throw the user out of the tree
    /// they were working in.
    ///
    /// (This expectation changed with the "opening selects nothing" rule. It used to expect the second collapse to
    /// reach the rail, which is only correct while the column is owned by an open module.)
    /// </summary>
    public static void CollapseWalksBackOneLevelAtATime()
    {
        var tree = new ToolTreeState();
        tree.OpenEntry("rukari.a", "stage");

        Check.Equal(1, tree.CollapseOneLevel(), "The first collapse closes the panel and keeps the module open.");
        Check.Equal("rukari.a", tree.OpenModuleId);
        Check.True(tree.OpenEntryId == null, "The entry is gone after one collapse.");

        Check.Equal(1, tree.CollapseOneLevel(), "The second collapse closes the module and keeps the column out.");
        Check.True(tree.OpenModuleId == null, "No highlighted button is left over an empty column.");
        Check.True(tree.IsExpanded, "The column the user opened stays out.");

        Check.Equal(0, tree.CollapseOneLevel(), "The third collapse puts the column away.");
        Check.True(tree.OpenEntryId == null && tree.OpenModuleId == null && !tree.IsExpanded,
            "Every level is closed after the last collapse.");

        Check.Equal(0, tree.CollapseOneLevel(), "Collapsing an already collapsed rail is a no-op.");
    }

    public static void UnregisteredModulesCannotStayOpen()
    {
        var tree = new ToolTreeState();
        tree.OpenEntry("rukari.a", "stage");

        Check.True(!tree.Synchronize(TwoModules), "A module that is still registered keeps its state.");

        Check.True(tree.Synchronize(new[] { new ToolModule("rukari.b", "B", "camera") }),
            "Unloading the open module must report a change.");
        Check.Equal(0, tree.Depth, "An unloaded module cannot leave a panel open over the editor.");
        Check.True(!tree.IsExpanded, "An unloaded module leaves the rail collapsed.");

        var collapsed = new ToolTreeState();
        Check.True(!collapsed.Synchronize(Array.Empty<ToolModule>()),
            "A collapsed rail has nothing to reconcile.");
    }

    /// <summary>
    /// A leaf whose provider unloaded must close, but the column the user opened stays: dropping all the way to
    /// the rail would make a mod reload throw the user out of the tree they were working in.
    /// </summary>
    public static void AnUnloadedEntryClosesThePanelAndKeepsTheColumn()
    {
        var entries = new[]
        {
            new ToolEntry("stage", "立绘操控", "card"),
            new ToolEntry("camera", "镜头操控", "camera")
        };
        var tree = new ToolTreeState();
        tree.OpenEntry("rukari.a", "stage");

        Check.True(!tree.Synchronize(TwoModules, entries), "A registered entry keeps its panel.");
        Check.True(!tree.Synchronize(TwoModules, null),
            "A caller that cannot read the entries must not clear a live one.");
        Check.Equal(2, tree.Depth);

        Check.True(tree.Synchronize(TwoModules, new[] { entries[1] }),
            "Unloading the open entry's provider must report a change.");
        Check.Equal(1, tree.Depth, "The panel closes, the module's column stays.");
        Check.Equal("rukari.a", tree.OpenModuleId, "The user keeps their place in the rail.");
        Check.True(tree.OpenEntryId == null, "No leaf is open any more.");

        tree.OpenEntry("rukari.a", "camera");
        Check.True(tree.Synchronize(TwoModules, Array.Empty<ToolEntry>()),
            "A module that publishes no entries at all cannot keep one open.");
        Check.Equal(1, tree.Depth, "The column survives an empty module as well.");
    }

    /// <summary>
    /// The depth the service owns decides which columns exist: the big icon is permanent, the module column
    /// appears with it, and the entries column only for a module that publishes several entries. Opening a leaf
    /// never moves the panel sideways, so the sheet cannot jump under the pointer.
    /// </summary>
    public static void TreeDepthDecidesHowManyColumnsExist()
    {
        var modules = new[] { TwoModules[0] };
        var entries = new[] { new ToolEntry("stage", "立绘操控", "card") };
        var layout = ToolDrawerLayout.Measure(1, 4, 4, true, true, "摘要", "状态");

        foreach (var size in new[] { (2560, 1541), (1920, 1080), (1280, 720) })
        {
            ToolDrawerPlacement rail = layout.PlaceTree(size.Item1, size.Item2, modules, 0,
                Array.Empty<ToolEntry>(), -1, treeOpen: false);
            ToolDrawerPlacement column = layout.PlaceTree(size.Item1, size.Item2, modules, 0, entries, -1);
            ToolDrawerPlacement leaf = layout.PlaceTree(size.Item1, size.Item2, modules, 0, entries, 0);

            Check.Equal(0, rail.Entries.Count, "A closed tree draws no entries column.");
            Check.Equal(0, rail.EntryColumn.Width, "A closed tree has no entries column to guard.");
            Check.Equal(1, column.Entries.Count, "An open multi-entry module draws one row per entry.");
            Check.Equal(1, leaf.Entries.Count, "Opening a leaf must not add or drop rows.");
            Check.Equal(column.Entries[0], leaf.Entries[0], "Opening a leaf must not move the row it came from.");
            Check.Equal(column.Panel, leaf.Panel,
                "Opening a leaf must not move the panel: the entries column is already showing.");
            float width = ToolDrawerLayout.ColumnWidthFor(modules);
            Check.Equal(ToolDrawerLayout.PanelXFor(width, showEntries: true), leaf.Panel.X,
                "With a column showing, the panel follows it.");
            Check.Equal(ToolDrawerLayout.PanelXFor(width, showEntries: false), rail.Panel.X,
                "With no column, the panel takes the column's place instead of leaving a gap.");
        }
    }

    /// <summary>
    /// The fail-closed guard band is what stops a wrong rectangle from letting a click reach the official editor,
    /// which reads a click on empty space as "close the node editor". While the middle column is showing, the
    /// band has to cover it; it must never reach into the panel, where a real control lives.
    /// </summary>
    public static void GuardBandCoversTheEntryColumnButNeverThePanel()
    {
        var modules = new[] { TwoModules[0], TwoModules[1] };
        var entries = new[] { new ToolEntry("stage", "立绘操控"), new ToolEntry("camera", "镜头操控") };
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");

        foreach (var size in new[] { (2560, 1541), (1920, 1080), (1280, 720), (800, 600) })
        {
            ToolDrawerPlacement open = layout.PlaceTree(size.Item1, size.Item2, modules, 0, entries, 0);
            float width = ToolDrawerLayout.ColumnWidthFor(modules);
            Check.Equal(ToolDrawerLayout.IconColumnX, open.CatchAll.X, "The band starts at the icon column.");
            Check.Equal(ToolDrawerLayout.EntryColumnRight(width), open.CatchAll.X + open.CatchAll.Width,
                "The band reaches the entries column's right edge, which is where the panel begins.");
            Check.True(open.CatchAll.X + open.CatchAll.Width <= open.Panel.X + .01,
                "The band must never reach into the panel.");
            foreach (ToolInputRect row in open.Entries)
            {
                Check.True(row.X >= open.CatchAll.X - .01 && row.X + row.Width <= open.CatchAll.X + open.CatchAll.Width + .01,
                    $"Every entry row lives inside the band; row {row}, band {open.CatchAll}.");
                Check.True(row.Y >= open.CatchAll.Y - .01 && row.Y + row.Height <= open.CatchAll.Y + open.CatchAll.Height + .01,
                    $"The band covers the whole column, including the gap between rows; row {row}, band {open.CatchAll}.");
            }

            ToolDrawerPlacement closed = layout.PlaceTree(size.Item1, size.Item2, modules, 0,
                Array.Empty<ToolEntry>(), -1, treeOpen: false);
            Check.Equal(ToolDrawerLayout.ModuleColumnRight(ToolDrawerLayout.ColumnWidthFor(modules)) - ToolDrawerLayout.IconColumnX,
                closed.CatchAll.Width,
                "With no entries column the band still covers the module buttons that can pop out beside the icon, "
                + "because those are exactly where the first press lands.");
        }
    }

    /// <summary>
    /// The configuration that actually ships today: one mod per button and one page each, so no entries column is
    /// drawn at all. Pins the real numbers a screenshot can be compared against, and the user's own layout rule
    /// that the tools pop out from the LEFT and leave the right-hand official workbench clear.
    /// </summary>
    public static void ShippedTwoModuleTreeStaysInTheLeftBand()
    {
        var modules = new[] { TwoModules[0], TwoModules[1] };
        // A module with one page publishes no column: its button opens the panel directly. This is the shipped
        // configuration, so it is the case a screenshot has to match.
        IReadOnlyList<ToolEntry> column = Array.Empty<ToolEntry>();
        var layout = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态");

        foreach (var size in new[] { (2560, 1440), (1920, 1080) })
        {
            ToolDrawerPlacement rail = layout.PlaceTree(size.Item1, size.Item2, modules, 0,
                column, -1, treeOpen: false);
            ToolDrawerPlacement open = layout.PlaceTree(size.Item1, size.Item2, modules, 0, column, -1);

            // The big icon is the whole rail while the tree is closed; the module buttons appear beside it.
            Check.Equal(2, rail.Children.Count, "One button per mod is placed even while the tree is closed.");
            Check.Equal(rail.Master, open.Master, "Opening the tree must not move the big icon.");
            Check.Equal(0, rail.Entries.Count, "A single-entry module never draws an entries column.");
            Check.Equal(0, open.Entries.Count, "…and that does not change when the tree opens.");
            Check.Equal(ToolDrawerLayout.PanelXFor(ToolDrawerLayout.ColumnWidthFor(modules), showEntries: false),
                open.Panel.X, "With no column the panel takes its place.");
            Check.Equal(ToolDrawerLayout.ModuleColumnX + ToolDrawerLayout.ModuleCardPadding, rail.Children[0].X,
                "Module rows pop out to the right of the big icon, inset inside their own card.");

            // The whole tree hugs the left edge on the screens this product runs on, so the official editor keeps
            // the right half of the window. (On a window narrower than the columns the sheet necessarily reaches
            // the middle; the widths themselves are unchanged by the tree.)
            Check.True(open.Panel.X + open.Panel.Width < size.Item1 / 2f,
                $"The tree stays in the left band; panel {open.Panel}, window {size.Item1}.");
            Check.True(open.CatchAll.X + open.CatchAll.Width < open.Panel.X + .01,
                "The fail-closed band stays out of the panel.");
        }
    }

    /// <summary>
    /// All three columns pop out to the RIGHT of each other: the big icon, the module buttons, the entries of a
    /// multi-entry module, then the panel. Each one has to fit without overlapping the next.
    /// </summary>
    public static void TreeColumnsPopOutToTheRightWithoutOverlapping()
    {
        var modules = new[] { TwoModules[0], TwoModules[1] };
        var entries = new[]
        {
            new ToolEntry("stage", "立绘操控", "card"),
            new ToolEntry("camera", "镜头操控", "camera"),
            new ToolEntry("text", "屏幕文字", "magnify")
        };
        var layout = ToolDrawerLayout.Measure(3, 4, 4, true, true, "摘要", "状态");

        foreach (var size in new[] { (2560, 1541), (1920, 1080), (1280, 720) })
        {
            ToolDrawerPlacement open = layout.PlaceTree(size.Item1, size.Item2, modules, 0, entries, 0);
            ToolDrawerPlacement column = layout.PlaceTree(size.Item1, size.Item2, modules, 0, entries, -1);
            ToolDrawerPlacement none = layout.PlaceLeft(size.Item1, size.Item2, modules, 0);

            // Big icon -> module buttons -> entries -> panel, strictly left to right.
            Check.True(open.Master.X + open.Master.Width <= open.Children[0].X + .01,
                $"The module buttons sit right of the big icon; master {open.Master}, button {open.Children[0]}.");
            Check.True(open.Children[0].X + open.Children[0].Width <= open.EntryColumn.X + .01,
                $"The entry column sits right of the module buttons; button {open.Children[0]}, column {open.EntryColumn}.");
            Check.True(open.EntryColumn.X + open.EntryColumn.Width <= open.Panel.X + .01,
                $"The panel sits right of the entry column; column {open.EntryColumn}, panel {open.Panel}.");
            Check.True(open.Panel.X + open.Panel.Width <= size.Item1 + .01,
                "The whole tree still has to fit inside the window.");

            float width = ToolDrawerLayout.ColumnWidthFor(modules);
            // One row per entry, inside the column, in registration order from the top down.
            Check.Equal(entries.Length, open.Entries.Count, "Every entry gets a row.");
            for (int i = 0; i < open.Entries.Count; i++)
            {
                ToolInputRect row = open.Entries[i];
                Check.Equal(ToolDrawerLayout.EntryColumnX(width), row.X, "Every row shares the column's left edge.");
                Check.Equal(width, row.Width, "Every row fills the column's width.");
                Check.True(row.Y >= open.EntryColumn.Y - .01
                    && row.Y + row.Height <= open.EntryColumn.Y + open.EntryColumn.Height + .01,
                    $"Row {i} must stay inside the column.");
                if (i > 0)
                {
                    Check.True(row.Y + row.Height <= open.Entries[i - 1].Y - .01,
                        "Entry rows are stacked top down in registration order.");
                }
            }

            // Pressing a row opens the panel, and the panel was already at its final edge, so nothing jumps.
            Check.Equal(column.Panel, open.Panel, "Opening a leaf must not move the panel sideways.");
            Check.Equal(0, none.Entries.Count, "With no entries passed there is no entries column.");
            Check.Equal(0, none.EntryColumn.Width, "The entries column is absent when no entries are passed.");
            Check.Equal(ToolDrawerLayout.PanelXFor(width, showEntries: false), none.Panel.X,
                "With no column, the panel takes its place.");
        }
    }
}
