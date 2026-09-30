using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

internal static class ToolPresentationTests
{
    internal static void NodeToolsRequireAnOpenLoadedInspector()
    {
        // A loaded node without a selected line is still a valid place to show disabled tools.
        var open = new EditorWorkspaceVisibilityState(true, true, true, true, true, true, false);
        Check.True(open.IsNodeEditorVisible, "Opening a node must expose tools before the first line is selected.");
        var outside = new[]
        {
            open with { RuntimeReady = false }, // catalogue/loading
            open with { InspectorActive = false }, // graph after closing a node
            open with { ContainerActive = false },
            open with { IsCurrentInspector = false }, // another kind of node
            open with { HasScriptNode = false },
            open with { PanelsVisible = false }, // NGUI alpha-hidden, but GameObject still active
            open with { IsTransitioning = true }
        };
        foreach (var state in outside)
            Check.True(!state.IsNodeEditorVisible, "No tools or input region may survive outside the loaded Script inspector.");
    }

    internal static void ButtonTextHasContrastInEveryVisualState()
    {
        var pairs = new[] { ToolPalette.Panel, ToolPalette.Header, ToolPalette.Normal,
            ToolPalette.Selected, ToolPalette.Disabled, ToolPalette.Primary, ToolPalette.Undo };
        foreach (var pair in pairs)
        {
            double a = Luminance(pair.Background), b = Luminance(pair.Foreground);
            double contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
            Check.True(contrast >= 4.5, $"Small button labels need readable text in every state; found {contrast:F2}:1.");
        }
    }

    internal static void EmptyVoicePanelDoesNotReserveEightEmptyRows()
    {
        var empty = ToolDrawerLayout.Measure(1, 0, 4, true, true, "请选择一句台词。", "选择台词后刷新语音列表。");
        var populated = ToolDrawerLayout.Measure(1, 8, 4, true, true, "请选择一句台词。", "选择台词后刷新语音列表。");
        Check.True(empty.EmptyList && !empty.Pagination, "An empty catalogue needs a short explanation, not pagination.");
        Check.True(empty.Height < 460, "The empty voice page should be a compact panel.");
        Check.True(populated.Height - empty.Height > 250, "Empty resource rows must not create a large blank sheet.");
    }

    internal static void DrawerFitsScreensAndScalesWithLongContent()
    {
        var views = new[]
        {
            ToolDrawerLayout.Measure(1, 0, 4, true, true, "请选择一句台词。", "尚未导入声音。"),
            ToolDrawerLayout.Measure(1, 9000, 8, true, true, new string('字', 900), new string('字', 600)),
            ToolDrawerLayout.Measure(16, 8, 8, true, true, "摘要", "状态")
        };
        foreach (var size in new[] { (2560, 1541), (2048, 1233), (1280, 720), (800, 600) })
        foreach (var view in views)
        {
            float scale = view.ScaleFor(size.Item1, size.Item2);
            Check.True(float.IsFinite(scale) && scale > 0, "Layout must have a usable scale.");
            Check.True(ToolDrawerLayout.LeftPanelEdgePixels(size.Item1, size.Item2) < size.Item1,
                "The reserved rail must still fit on a narrow window.");
            Check.True((view.Height + 40) * scale <= size.Item2 + .01f,
                "The last action/status must remain on screen, including long provider content.");
        }
    }

    internal static void ChinesePageTitlesFitAndLongLabelsStayOnOneLine()
    {
        foreach (string title in new[] { "更多的画面效果", "人物配音支持", "记忆大厅支持" })
            Check.Equal(title, ToolDrawerLayout.FitSingleLine(title, 16, ToolDrawerLayout.ContentWidth - 56),
                "User-chosen Chinese titles must be visible in full in the panel header.");
        foreach (string title in new[] { "配音", "画面", "大厅" })
            Check.Equal(title, ToolDrawerLayout.FitSingleLine(title, 15, ToolDrawerLayout.RailButtonWidth - 8),
                "A rail button label must stay on one line.");
        string longLabel = "这是一个非常长的声音文件名😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀";
        string fitted = ToolDrawerLayout.FitSingleLine(longLabel, 18, 180);
        Check.True(fitted.EndsWith('…') && fitted.Length < longLabel.Length, "Long resource labels need a single-line ellipsis.");
        Check.True(!fitted.Contains('\uFFFD'), "A label must not split a Unicode character.");
        Check.True(!ToolDrawerLayout.FitSingleLine("第一行\n第二行", 18, 300).Contains('\n'), "Buttons must not wrap provider newlines.");
    }

    /// <summary>One child icon per enabled mod, a master icon that never moves, and a panel that stays on screen.</summary>
    internal static void LeftSidebarLeavesTheOfficialRightWorkspaceClear()
    {
        var views = new[]
        {
            ToolDrawerLayout.Measure(1, 0, 4, true, true, "请选择一句台词。", "尚未导入声音。"),
            ToolDrawerLayout.Measure(1, 9000, 8, true, true, new string('字', 900), new string('字', 600))
        };
        foreach (var size in new[] { (2560, 1541), (2048, 1233), (1280, 720), (800, 600), (640, 480) })
        foreach (var view in views)
        foreach (var modulesUnderTest in new[] { OneModule, FourModules, SixteenModules })
        {
            ToolDrawerPlacement placement = view.PlaceLeft(size.Item1, size.Item2, modulesUnderTest, 0);
            Check.True(placement.Panel.IsValid && placement.Master.IsValid, "Both left surfaces need valid screen bounds.");
            Check.True(placement.Children.Count <= modulesUnderTest.Length && placement.Children.All(c => c.IsValid),
                "Every placed child icon must be valid, and the drawer may never invent one.");
            Check.True(placement.Master.X >= 0 && placement.Master.X < size.Item1 * .05f,
                "The icon column belongs next to the left edge.");
            Check.True(placement.Master.X + placement.Master.Width < placement.Panel.X,
                "The icon column must have its own band, outside the panel's hit area.");
            Check.True(placement.Master.Y + placement.Master.Height <= size.Item2 + .01,
                "The master icon must stay fully on screen; it is the only way in and out.");
            Check.True(placement.Panel.Width <= ToolDrawerLayout.Width + .01,
                "A wide screen must add margin, not a wider sheet.");
            Check.True(placement.Panel.X + placement.Panel.Width <= size.Item1 + .01,
                "The panel must stay inside the window.");
            Check.True(placement.Panel.Y >= 0 && placement.Panel.Y + placement.Panel.Height <= size.Item2 + .01,
                "Moving the drawer left must not push its actions outside the vertical screen bounds.");
            Check.Equal(ToolDrawerLayout.IconColumnX, placement.Master.X,
                "The icon column uses one fixed left edge so it can never drift into the panel.");
        }
    }

    /// <summary>
    /// The master icon is the permanent entry point, so it must never move, and the child stack must never
    /// overlap it. Children grow upward from the master.
    /// </summary>
    internal static void RailButtonsStayFixedWhileModulesOpenCloseAndResize()
    {
        var compact = ToolDrawerLayout.Measure(1, 0, 4, true, true, "请选择一句台词。", "等待选择。");
        var tall = ToolDrawerLayout.Measure(16, 8000, 8, true, true, new string('字', 900), new string('字', 600));
        foreach (var size in new[] { (2560, 1541), (1280, 720), (800, 600) })
        foreach (var modulesUnderTest in new[] { OneModule, FourModules, SixteenModules })
        {
            ToolDrawerPlacement collapsed = compact.PlaceLeft(size.Item1, size.Item2, modulesUnderTest, 0, expanded: false);
            ToolDrawerPlacement first = compact.PlaceLeft(size.Item1, size.Item2, modulesUnderTest, 0);
            ToolDrawerPlacement last = tall.PlaceLeft(size.Item1, size.Item2, modulesUnderTest, modulesUnderTest.Length - 1);
            Check.Equal(collapsed.Master, first.Master, "Opening the drawer must not move the master icon.");
            Check.Equal(first.Master, last.Master, "Switching module or page height must not move the master icon.");
            Check.Equal(first.Children[0], last.Children[0], "The module column must not move when the page height changes.");
            foreach (ToolInputRect child in first.Children)
                Check.True(child.X >= first.Master.X + first.Master.Width - .01,
                    $"Module buttons pop out to the RIGHT of the big icon; button {child}, icon {first.Master}.");
            Check.True(first.Panel.X == last.Panel.X, "Every module's panel opens at the same shared left edge.");
        }
        Check.True(tall.PlaceLeft(1280, 720, FourModules, 0).Scale <= compact.PlaceLeft(1280, 720, FourModules, 0).Scale,
            "A taller page must never enlarge the panel.");
    }

    /// <summary>
    /// Pins the coordinate convention the drawer is drawn in: physical pixels with the origin at the BOTTOM-left
    /// and Y growing upward. The canvas controls are created with anchorMin = anchorMax = pivot = (0,0), so a
    /// value computed as "pixelHeight - margin - height" lands at the TOP of the screen. Nothing compared the
    /// placement against that convention, so the whole icon column was drawn at the top-left.
    /// </summary>
    internal static void RailPlacementUsesBottomUpScreenCoordinates()
    {
        foreach (var size in new[] { (2560, 1494), (1920, 1080), (1280, 720), (800, 600) })
        foreach (var modulesUnderTest in new[] { OneModule, FourModules })
        {
            ToolDrawerPlacement placement = ToolDrawerLayout
                .Measure(1, 4, 4, true, true, "摘要", "状态")
                .PlaceLeft(size.Item1, size.Item2, modulesUnderTest, 0);
            ToolInputRect master = placement.Master;
            int visibleRows = ToolDrawerLayout.VisibleModuleRows(size.Item2, modulesUnderTest.Length);

            // The group is centred on the left edge, not pinned to a corner: the official editor has controls
            // near BOTH left corners, so the middle is the only free place for a permanent rail. The group is the
            // big icon plus the module card beside it, so its height is the taller of the two.
            float groupHeight = ToolDrawerLayout.RailGroupHeight(visibleRows);
            float groupBottom = ToolDrawerLayout.RailGroupBottom(size.Item2, visibleRows);
            float groupCentre = groupBottom + groupHeight / 2f;
            Check.Equal(visibleRows, placement.Children.Count,
                "The card shows every registered module while they fit, and never more than fit.");
            Check.True(Math.Abs(groupCentre - size.Item2 / 2f) <= ToolDrawerLayout.RailGuardMargin,
                $"The icon group is centred on the left edge; centre {groupCentre}, screen centre {size.Item2 / 2f}.");
            Check.Equal(ToolDrawerLayout.MasterBounds(size.Item1, size.Item2, modulesUnderTest.Length), master,
                "PlaceLeft and MasterBounds must agree on where the permanent icon is.");
            Check.True(master.Y + master.Height < size.Item2 - ToolDrawerLayout.RailGuardMargin,
                "A centred rail keeps clear of both screen corners.");
            Check.Equal(groupBottom + (groupHeight - master.Height) / 2f, master.Y,
                "The big icon is centred inside its group, beside the module card.");

            // The card starts on the group's own bottom line and holds its rows inset from its edge, so pressing
            // the big icon grows a list instead of pushing the icon around.
            Check.Equal(groupBottom, placement.Card.Y, "The module card starts on the group's own bottom line.");
            Check.Equal(ToolDrawerLayout.ModuleColumnX, placement.Card.X, "The card pops out right of the big icon.");
            Check.Equal(ToolDrawerLayout.ModuleCardHeight(visibleRows), placement.Card.Height,
                "The card is as tall as its title band plus the rows it shows.");
            Check.True(placement.Card.Height >= ToolDrawerLayout.ModuleCardHeaderHeight,
                "Even a card with no rows at all keeps its title band.");
            Check.Equal(placement.Card.Y + placement.Card.Height,
                placement.CardHeader.Y + placement.CardHeader.Height, "The title band is the card's top edge.");
            Check.Equal(placement.CardHeader.Y + (ToolDrawerLayout.ModuleCardHeaderHeight - placement.CardCollapse.Height) / 2f,
                placement.CardCollapse.Y, "The collapse control is centred inside the title band.");
            Check.True(placement.CardCollapse.X + placement.CardCollapse.Width
                    <= placement.Card.X + placement.Card.Width - ToolDrawerLayout.ModuleCardRightPadding + .01,
                "The collapse control stays inside the card's right inset.");
            float expected = placement.Card.Y + ToolDrawerLayout.ModuleCardPadding + ToolDrawerLayout.ModuleCardBottomPadding
                + placement.Children.Count * ToolDrawerLayout.ModuleButtonHeight
                + (placement.Children.Count - 1) * ToolDrawerLayout.ModuleButtonGap;
            foreach (ToolInputRect child in placement.Children)
            {
                Check.Equal(ToolDrawerLayout.ModuleColumnX + ToolDrawerLayout.ModuleCardPadding, child.X,
                    "Every module row shares one inset inside the card, right of the big icon.");
                Check.True(child.X >= master.X + master.Width - .01,
                    $"A module row must not cover the big icon; row {child}, icon {master}.");
                Check.Equal(ToolDrawerLayout.ModuleButtonHeight, child.Height, "Module rows keep their own height.");
                Check.True(child.Height >= ToolDrawerLayout.OfficialPlateMinimumHeight,
                    "A module row must be tall enough for the official nine-sliced plate to keep its border.");
                Check.True(child.Y >= placement.Card.Y + ToolDrawerLayout.ModuleCardBottomPadding - .01,
                    $"Every row is drawn inside the card's own padding; row {child}, card {placement.Card}.");
                Check.True(child.Y + child.Height <= placement.CardHeader.Y + .01,
                    $"Every row is drawn under the title band; row {child}, band {placement.CardHeader}.");
            }
            // Rows read top down from under the header, which is what makes the card a list rather than a stack
            // growing out of the icon. One row pitch serves every mod, however many are installed.
            for (int i = 1; i < placement.Children.Count; i++)
                Check.Equal(placement.Children[i - 1].Y - ToolDrawerLayout.ModuleRowPitch, placement.Children[i].Y,
                    "Module rows are stacked top down at a fixed pitch.");
            if (placement.Children.Count != 0)
                Check.Equal(placement.CardHeader.Y, expected,
                    "The rows fill the card between its own padding and its title band.");

            // The guard band hugs the group and grows with it, so a new module widens its own protection. Its top
            // is the TALLEST surface drawn, which is the card's title band rather than its first row: a band that
            // stopped at the rows would leave the header uncovered. On a screen too short for the group the band
            // clamps to the screen instead, which is also correct.
            float groupTop = Math.Max(master.Y + master.Height, placement.Card.Y + placement.Card.Height);
            Check.Equal(Math.Max(0f, groupBottom - ToolDrawerLayout.RailGuardMargin),
                placement.CatchAll.Y, "The guard band starts one margin below the group.");
            Check.Equal(
                Math.Min(size.Item2, groupTop + ToolDrawerLayout.RailGuardMargin),
                placement.CatchAll.Y + placement.CatchAll.Height,
                "The guard band ends one margin above the group.");
            Check.True(placement.CatchAll.Y + placement.CatchAll.Height <= size.Item2 + .01,
                "The guard band stays inside the screen.");
        }
    }

    /// <summary>
    /// The rail's own icon must resolve to its own emblem. It once fell through to the default glyph and showed
    /// an unrelated gear while the atlas already carried the phone the user picked.
    /// </summary>
    internal static void MasterModuleResolvesToItsOwnGlyph()
    {
        Check.Equal("master", RailIconStyle.GlyphFor(RailIconStyle.MasterModuleId),
            "The rail's own module must map to its own glyph, not to the fallback.");
        Check.True(RailIconStyle.Glyphs.Contains(RailIconStyle.GlyphFor(RailIconStyle.MasterModuleId)),
            "Every glyph a module can resolve to must be one the painter knows how to draw.");
        foreach (string glyph in RailIconStyle.Glyphs)
            Check.True(RailIconStyle.GlyphFor(glyph) == glyph,
                $"A glyph name must map to itself so an icon id can name a glyph directly; '{glyph}' did not.");
        Check.Equal(RailIconStyle.DefaultGlyph, RailIconStyle.GlyphFor("something-unknown"),
            "An unknown icon id still falls back to the default glyph.");
        Check.Equal("master", RailIconStyle.GlyphFor("MASTER"),
            "The glyph lookup is case-insensitive, since mod authors write ids freely.");
    }

    /// <summary>
    /// Every piece of panel chrome must sit inside the drawn panel and keep the panel's padding. The close
    /// button was once placed at ContentWidth + 8, which pushed its right edge four units past the panel's own
    /// width, so the × hung over the frame.
    /// </summary>
    internal static void PanelChromeStaysInsideTheFrame()
    {
        foreach (float panelHeight in new[] { 120f, 300f, 777f, 2000f })
        {
            ToolInputRect close = ToolDrawerLayout.CloseBounds(panelHeight);
            ToolInputRect title = ToolDrawerLayout.TitleBounds(panelHeight);

            Check.True(ToolDrawerLayout.IsInsidePanel(close, panelHeight),
                $"The close button must stay inside the panel; close {close}, height {panelHeight}.");
            Check.True(ToolDrawerLayout.IsInsidePanel(title, panelHeight),
                $"The title must stay inside the panel; title {title}, height {panelHeight}.");
            Check.Equal(ToolDrawerLayout.Width - ToolDrawerLayout.Padding,
                close.X + close.Width,
                "The close button is flush with the panel's right padding, not past the frame.");
            Check.Equal(panelHeight - ToolDrawerLayout.Padding,
                close.Y + close.Height,
                "The close button is flush with the panel's top padding.");
            Check.Equal(ToolDrawerLayout.CloseButtonSize, close.Width, "The close button stays square.");
            Check.Equal(close.Height, close.Width, "The close button stays square.");
            Check.True(!Overlaps(title, close),
                $"The title must not reach under the close button; title {title}, close {close}.");
            Check.Equal(ToolDrawerLayout.Width / 2f, title.X + title.Width / 2f,
                "A centered title stays at the sheet center even though only the right side has a close control.");
            Check.True(close.Width >= 32 && close.Height >= 32,
                "The close button must stay big enough to hit at the smallest panel scale.");

            // The real panel heights this drawer produces, measured from the layout itself.
            foreach (int rows in new[] { 0, 8 })
            {
                var measured = ToolDrawerLayout.Measure(3, rows, 8, true, true, "摘要", "状态");
                Check.True(ToolDrawerLayout.IsInsidePanel(ToolDrawerLayout.CloseBounds(measured.Height), measured.Height),
                    "The close button must stay inside a measured panel too.");
            }
        }
    }

    /// <summary>Two different mods must not receive the same accent colour.</summary>
    internal static void RailIconAccentsAreDistinctPerModule()
    {
        var seen = new HashSet<int>();
        foreach (string module in new[] { "rukari.charactervoice", "rukari.moreeffects", "rukari.memorylobby", "third.party.mod" })
        {
            ToolColor colour = RailIconStyle.AccentFor(module);
            foreach (double channel in new[] { colour.R, colour.G, colour.B })
                Check.True(channel is >= 0 and <= 1, "An accent colour channel must stay in range.");
            Check.True(seen.Add((int)(colour.R * 255) << 16 | (int)(colour.G * 255) << 8 | (int)(colour.B * 255)),
                "Each module needs a distinct accent colour so an unknown icon is still identifiable.");
        }
        Check.Equal(RailIconStyle.AccentFor("same.module"), RailIconStyle.AccentFor("same.module"),
            "An accent colour must be stable for the same module id.");
        Check.True(RailIconStyle.AccentKey("same.module") == RailIconStyle.AccentKey("same.module")
            && RailIconStyle.AccentKey("same.module").Length == 6,
            "The accent key is the cache key for the painted glyph and must be a stable six-digit value.");
    }

    /// <summary>Built-in glyph names must map predictably, and an unknown future id must still get an emblem.</summary>
    internal static void RailGlyphsResolveForKnownAndFutureModules()
    {
        Check.Equal("wave", RailIconStyle.GlyphFor("wave"));
        Check.Equal("wave", RailIconStyle.GlyphFor("VOICE"));
        Check.Equal("camera", RailIconStyle.GlyphFor("effects"));
        Check.Equal("card", RailIconStyle.GlyphFor("memory"));
        Check.Equal("magnify", RailIconStyle.GlyphFor("search"));
        Check.Equal("gear", RailIconStyle.GlyphFor("gear"));
        // A mod we have never seen must not end up with a blank button.
        Check.Equal(RailIconStyle.DefaultGlyph, RailIconStyle.GlyphFor("some-future-module-icon"));
        Check.Equal(RailIconStyle.DefaultGlyph, RailIconStyle.GlyphFor(null));
        Check.Equal(RailIconStyle.DefaultGlyph, RailIconStyle.GlyphFor("   "));
        foreach (string glyph in RailIconStyle.Glyphs)
            Check.Equal(glyph, RailIconStyle.GlyphFor(glyph), "Every advertised glyph must round-trip through GlyphFor.");
    }

    /// <summary>
    /// The toggle handle must never sit under a module button. It once did, and because the handle was resolved
    /// last in the pointer pass it swallowed the top module's clicks — the bottom button still worked, so it
    /// looked like only one mod was registered.
    /// </summary>
    internal static void ToggleHandleNeverOverlapsAModuleButton()
    {
        var view = ToolDrawerLayout.Measure(1, 4, 4, true, true, "摘要", "状态");
        foreach (var size in new[] { (2560, 1440), (2048, 1152), (1920, 1080), (1280, 720), (800, 600), (640, 480) })
        foreach (var modulesUnderTest in new[] { OneModule, FourModules, SixteenModules })
        {
            ToolDrawerPlacement placement = view.PlaceLeft(size.Item1, size.Item2, modulesUnderTest, 0);
            ToolInputRect master = placement.Master;
            int visibleRows = ToolDrawerLayout.VisibleModuleRows(size.Item2, modulesUnderTest.Length);
            float groupHeight = ToolDrawerLayout.RailGroupHeight(visibleRows);
            float groupBottom = ToolDrawerLayout.RailGroupBottom(size.Item2, visibleRows);
            Check.True(master.IsValid, "The master icon needs valid screen bounds.");
            Check.True(master.Y >= 0 && master.Y + master.Height <= size.Item2 + .01,
                $"The master icon must stay fully on screen; master {master}, screen height {size.Item2}.");
            Check.True(master.X >= 0 && master.X + master.Width < placement.Panel.X + .01,
                "The master icon belongs in the left strip, not under the panel.");
            Check.True(master.Y >= 0 && master.Y + master.Height <= size.Item2 + .01,
                "The master icon must stay fully on screen.");
            Check.True(master.Y >= ToolDrawerLayout.MasterScreenMargin - .01,
                $"The rail must keep its margin from the bottom edge; master {master}.");
            // Centring is asserted exactly where the group always fits; on a screen too short for the whole group
            // the big icon snaps back inside the bottom margin, which is the long-standing rule that the one
            // permanent control is never pushed off screen.
            float groupCentre = groupBottom + groupHeight / 2f;
            Check.True(Math.Abs(groupCentre - size.Item2 / 2f) <= ToolDrawerLayout.RailGuardMargin
                || groupHeight > size.Item2,
                $"A group that fits belongs in the middle of the left edge; centre {groupCentre}, screen centre {size.Item2 / 2f}.");
            // Module buttons pop out to the right of the big icon, so they must never intersect it.
            foreach (ToolInputRect child in placement.Children)
                Check.True(!Overlaps(master, child),
                    $"A module button must never overlap the big icon; master {master}, child {child}.");
            // The published guard band hugs the icon group: it covers the group plus one margin on each side, so
            // a click beside an icon can never reach the official editor and close the node editor, while the
            // official controls near the screen corners stay clickable. It never reaches into the panel.
            ToolInputRect strip = placement.CatchAll;
            Check.True(strip.IsValid, "The rail guard band needs valid bounds.");
            Check.True(strip.Y >= 0 && strip.Y + strip.Height <= size.Item2 + .01,
                $"The guard band stays inside the screen; strip {strip}, screen {size.Item2}.");
            Check.True(strip.Y <= master.Y + .01,
                "The guard band must start at or below the lowest icon so nothing beside the rail falls through.");
            float groupTop = Math.Max(master.Y + master.Height, placement.Card.Y + placement.Card.Height);
            foreach (ToolInputRect child in placement.Children)
                groupTop = Math.Max(groupTop, child.Y + child.Height);
            Check.True(strip.Y + strip.Height >= groupTop - .01,
                $"The guard band must reach the top of the group, which is the TALLEST thing drawn and not merely "
                + $"the last button; strip top {strip.Y + strip.Height}, group top {groupTop}.");
            Check.True(strip.Height <= (groupTop - strip.Y) + ToolDrawerLayout.RailGuardMargin + .01,
                $"The guard band hugs the icon group instead of blanketing the whole column, which would swallow "
                + "the official controls near the screen corners.");
            Check.True(placement.Children.Count <= modulesUnderTest.Length,
                "The drawer may only hide module buttons that cannot fit on screen, never invent ones.");
            foreach (ToolInputRect child in placement.Children)
            {
                Check.True(child.Y >= 0 && child.Y + child.Height <= size.Item2 + .01,
                    $"Every placed module button must be fully on screen; child {child}, screen height {size.Item2}.");
            }
            Check.True(!Overlaps(strip, placement.Panel), "The guard band must never cover the panel.");
            Check.True(strip.X + strip.Width < placement.Panel.X + .01,
                "The rail strip must never reach into the panel.");
        }
    }

    /// <summary>
    /// The rail's small buttons wear a nine-sliced official plate. Unity clamps a slice border that is taller than
    /// the rectangle, which turns the corner into a semicircle and the whole button into a pill, so the geometry
    /// has to stay above that minimum; and the shipped titles have to fit the width measured for them.
    /// </summary>
    internal static void SharedButtonsFitTheOfficialPlate()
    {
        Check.True(ToolDrawerLayout.ModuleButtonHeight >= ToolDrawerLayout.OfficialPlateMinimumHeight,
            $"A module button is {ToolDrawerLayout.ModuleButtonHeight} tall but the plate needs "
            + $"{ToolDrawerLayout.OfficialPlateMinimumHeight} before its corner is clamped into a semicircle.");
        Check.True(ToolDrawerLayout.EntryRowHeight >= ToolDrawerLayout.OfficialPlateMinimumHeight,
            "An entry row wears the same plate and needs the same minimum height.");
        Check.True(ToolDrawerLayout.ModuleButtonMinimumWidth > ToolDrawerLayout.OfficialPlateMinimumHeight * 2,
            "A button must be WIDER than its two corner slices, or the plate has nothing left to stretch.");
        foreach (string title in new[] { "人物配音支持", "更多的画面效果", "记忆大厅支持" })
        {
            Check.Equal(title, ToolDrawerLayout.FitSingleLine(title, ToolDrawerLayout.ModuleButtonFontSize,
                    ToolDrawerLayout.WidthNeededFor(title) - 2 * ToolDrawerLayout.ModuleButtonLabelInset),
                $"A shipped module title must fit the button measured for it, in full: {title}.");
        }
    }

    /// <summary>
    /// The column is sized from the longest mod name, so a short name and a long one both get a button whose label
    /// fits without an ellipsis, and every button in the column shares one width so their labels line up on one
    /// left edge. The measurement is the same one the renderer uses, which keeps the two from disagreeing.
    /// </summary>
    internal static void ModuleColumnAdaptsToTheLongestName()
    {
        float shortName = ToolDrawerLayout.ColumnWidthFor(new[] { new ToolModule("m", "配音", "") });
        float longName = ToolDrawerLayout.ColumnWidthFor(new[] { new ToolModule("m", "更多的画面效果", "") });
        Check.True(shortName >= ToolDrawerLayout.ModuleButtonMinimumWidth,
            "A very short name still gets a usable button.");
        Check.True(longName > shortName, "A longer name gets a wider column.");
        Check.True(longName < ToolDrawerLayout.ModuleButtonMaximumWidth,
            "The shipped names are nowhere near the cap, so they are never truncated.");
        Check.Equal(ToolDrawerLayout.ModuleButtonMaximumWidth,
            ToolDrawerLayout.ColumnWidthFor(new[] { new ToolModule("m", new string('字', 200), "") }),
            "One absurdly long name must not take over the screen.");
        Check.Equal(ToolDrawerLayout.ModuleButtonMinimumWidth, ToolDrawerLayout.ColumnWidthFor(null),
            "A column with no modules to measure keeps its minimum width.");

        foreach (string title in new[] { "配音", "人物配音支持", "更多的画面效果", "画面效果与镜头控制设置" })
        {
            float column = ToolDrawerLayout.ColumnWidthFor(new[] { new ToolModule("m", title, "") });
            Check.Equal(title, ToolDrawerLayout.FitSingleLine(title, ToolDrawerLayout.ModuleButtonFontSize,
                    column - 2 * ToolDrawerLayout.ModuleButtonLabelInset),
                $"A measured column must show its own title in full: {title}.");
        }

        var modules = new[]
        {
            new ToolModule("a", "配音", ""),
            new ToolModule("b", "更多的画面效果", ""),
            new ToolModule("c", "记忆大厅支持", "")
        };
        float width = ToolDrawerLayout.ColumnWidthFor(modules);
        ToolDrawerPlacement placement = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态")
            .PlaceTree(1920, 1080, modules, 0, Array.Empty<ToolEntry>(), -1);
        foreach (ToolInputRect button in placement.Children)
            Check.Equal(width, button.Width, "Every module button is as wide as the longest name needs.");
        Check.Equal(ToolDrawerLayout.PanelXFor(width, showEntries: false), placement.Panel.X,
            "The panel follows the measured column, so a long name cannot make it overlap.");
    }

    /// <summary>
    /// A hosted page may ask for a sheet of its own width, which is what lets a ported editor line up with the
    /// native panel it replaces instead of being squeezed into the shared 620. These are the rules a migration
    /// depends on: a page with no opinion keeps the shared sheet, an absurd request is clamped, and a sheet that
    /// fits is drawn one canvas unit per pixel — which is what makes the page's own coordinates land where it
    /// expects them.
    /// </summary>
    internal static void HostedPageGetsItsOwnSheetWidth()
    {
        Check.Equal(ToolDrawerLayout.Width, ToolDrawerLayout.MeasureHosted(720f).PanelWidth,
            "A page that only implements the content interface keeps the shared sheet width.");
        Check.Equal(ToolDrawerLayout.Width, ToolDrawerLayout.MeasureHosted(720f, 0f).PanelWidth,
            "Zero means 'no opinion', never a zero-wide sheet.");
        Check.Equal(ToolDrawerLayout.Width, ToolDrawerLayout.MeasureHosted(720f, float.NaN).PanelWidth,
            "A non-finite width falls back to the shared sheet.");
        Check.Equal(ToolDrawerLayout.Width, ToolDrawerLayout.MeasureHosted(720f, -40f).PanelWidth,
            "A negative width falls back to the shared sheet.");
        Check.Equal(ToolDrawerLayout.MinimumHostedWidth, ToolDrawerLayout.MeasureHosted(720f, 1f).PanelWidth,
            "An absurdly narrow request is clamped up.");
        Check.Equal(ToolDrawerLayout.MaximumHostedWidth, ToolDrawerLayout.MeasureHosted(720f, 99999f).PanelWidth,
            "An absurdly wide request is clamped down.");

        var wide = ToolDrawerLayout.MeasureHosted(900f, 900f);
        Check.Equal(900f, wide.PanelWidth, "A usable request is used as given.");
        Check.Equal(900f - ToolDrawerLayout.Padding * 2, wide.InnerWidth,
            "The content rectangle is the sheet inside its own padding.");
        Check.Equal(ToolDrawerLayout.ContentWidth,
            ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态").InnerWidth,
            "A snapshot page keeps exactly the sheet it has always had.");

        // Chrome is placed from the sheet's own width, so a wide page's close button hugs that page's right edge
        // instead of the shared 620.
        foreach (ToolDrawerLayout sheet in new[]
        {
            ToolDrawerLayout.MeasureHosted(600f, 1f), wide, ToolDrawerLayout.MeasureHosted(600f, 99999f)
        })
        {
            ToolInputRect close = ToolDrawerLayout.CloseBounds(sheet.PanelWidth, sheet.Height);
            Check.True(ToolDrawerLayout.IsInsidePanel(close, sheet.PanelWidth, sheet.Height),
                $"The close button must stay inside its own sheet; sheet {sheet.PanelWidth}, close {close}.");
            Check.Equal(sheet.PanelWidth - ToolDrawerLayout.Padding, close.X + close.Width,
                "The close button follows the sheet it belongs to.");
            Check.True(ToolDrawerLayout.IsInsidePanel(
                    ToolDrawerLayout.TitleBounds(sheet.PanelWidth, sheet.Height), sheet.PanelWidth, sheet.Height),
                "The title must stay inside its own sheet.");
            Check.True(sheet.InnerWidth > ToolDrawerLayout.CloseButtonSize * 2,
                "Even the narrowest sheet keeps room for content beside its close button.");
        }
        // The width-aware chrome must agree with the shared-sheet chrome, or every existing page would move.
        Check.Equal(ToolDrawerLayout.CloseBounds(777f), ToolDrawerLayout.CloseBounds(ToolDrawerLayout.Width, 777f),
            "The width-aware close bounds are the same placement on the shared sheet.");
        Check.Equal(ToolDrawerLayout.TitleBounds(777f), ToolDrawerLayout.TitleBounds(ToolDrawerLayout.Width, 777f),
            "The width-aware title bounds are the same placement on the shared sheet.");

        // The numbers a ported editor will really ask for, on the resolutions it has to survive: its content is 720
        // story units wide, a constant fraction of the screen, and its height scales the same way.
        var modules = new[] { new ToolModule("rukari.moreeffects", "更多的画面效果", "camera") };
        foreach (var size in new[] { (3840, 2160), (2560, 1440), (1920, 1080) })
        {
            float pixelsPerStory = size.Item1 / 2960f;
            float height = Math.Max(900f, 2960f * size.Item2 / size.Item1 - 160f) * pixelsPerStory;
            float width = 720f * pixelsPerStory + ToolDrawerLayout.Padding * 2;
            ToolDrawerLayout sheet = ToolDrawerLayout.MeasureHosted(height, width);
            ToolDrawerPlacement placement = sheet.PlaceTree(size.Item1, size.Item2, modules, 0,
                Array.Empty<ToolEntry>(), -1);
            ToolDrawerPlacement shared = ToolDrawerLayout.Measure(1, 0, 4, true, true, "摘要", "状态")
                .PlaceTree(size.Item1, size.Item2, modules, 0, Array.Empty<ToolEntry>(), -1);
            Check.Equal(shared.Panel.X, placement.Panel.X,
                $"A wider sheet must not move the column the panel hangs off; screen {size}.");
            Check.Equal(1f, placement.Scale,
                "A sheet that fits is drawn one canvas unit per pixel, so a ported editor's own coordinates land "
                + $"where it expects them; screen {size}.");
            Check.Equal(sheet.PanelWidth, placement.Panel.Width, "The drawn sheet is exactly as wide as it asked.");
            Check.True(placement.Panel.X + placement.Panel.Width <= size.Item1 + .01f,
                $"The sheet must stay inside the window; screen {size}.");
            Check.True(placement.Panel.Y >= 0 && placement.Panel.Y + placement.Panel.Height <= size.Item2 + .01f,
                $"The sheet must stay inside the window vertically; screen {size}.");
            Check.True(placement.CatchAll.X + placement.CatchAll.Width < placement.Panel.X + .01f,
                "The fail-closed rail band must still never reach into the wider panel.");
        }

        // On a window too short for the group the rail scale drops below one and the sheet shrinks with it. The page
        // is expected to read the rectangle it actually got, so the layout must report the smaller one rather than
        // pretend the request was honoured.
        {
            ToolDrawerLayout sheet = ToolDrawerLayout.MeasureHosted(900f, 700f);
            ToolDrawerPlacement placement = sheet.PlaceTree(1280, 720, modules, 0, Array.Empty<ToolEntry>(), -1);
            Check.True(placement.Scale < 1f, "A sheet that cannot fit must shrink instead of leaving the screen.");
            Check.True(Math.Abs(placement.Panel.Width - sheet.PanelWidth * placement.Scale) < .01f,
                "The drawn width is the sheet width at the drawn scale, which is what the page is told in pixels.");
            Check.True(placement.Panel.X + placement.Panel.Width <= 1280f + .01f,
                "A shrunken sheet still stays inside the window.");
        }
    }

    /// <summary>
    /// The rectangle a hosted page is handed is the one number the migration of a ported editor depends on: the
    /// editor places its own canvas from that rectangle and divides by its own scale, so the division has to give
    /// back exactly the story units it was designed for. These are the real numbers, on the real resolutions,
    /// through the very helper the renderer uses to produce them.
    /// </summary>
    internal static void HostedContentRectangleRoundTripsIntoTheEditorsOwnCoordinates()
    {
        const float storyWidth = 2960f;
        const float editorWidth = 720f; // the editor's own panel, in story units
        var modules = new[] { new ToolModule("rukari.moreeffects", "更多的画面效果", "camera") };
        foreach (var size in new[] { (3840, 2160), (2560, 1440), (1920, 1080) })
        {
            float pixelsPerStory = size.Item1 / storyWidth;
            float storyHeight = storyWidth * size.Item2 / size.Item1;
            float wantedHeight = Math.Max(900f, storyHeight - 160f) * pixelsPerStory;
            ToolDrawerLayout sheet = ToolDrawerLayout.MeasureHosted(wantedHeight,
                editorWidth * pixelsPerStory + 2 * ToolDrawerLayout.Padding);
            ToolDrawerPlacement placement = sheet.PlaceTree(size.Item1, size.Item2, modules, 0,
                Array.Empty<ToolEntry>(), -1);
            ToolInputRect content = ToolDrawerLayout.ToScreenPixels(placement.Panel, placement.Scale,
                new ToolInputRect(ToolDrawerLayout.Padding, ToolDrawerLayout.Padding,
                    sheet.InnerWidth, sheet.ContentHeight));

            Check.Equal(1f, placement.Scale, $"A sheet that fits is drawn 1:1; screen {size}.");
            Check.True(Math.Abs(content.Width / pixelsPerStory - editorWidth) < .01f,
                $"Dividing the rectangle the page got by the page's own scale must give back exactly {editorWidth} "
                + $"story units, or a ported editor's coordinates move; screen {size}, got {content.Width / pixelsPerStory}.");
            Check.True(Math.Abs(content.Height - (wantedHeight - ToolDrawerLayout.Padding * 2
                    - ToolDrawerLayout.HeaderRowHeight)) < .01f,
                $"The content is the sheet minus the padding and the header the library now draws for the page; screen {size}.");
            Check.True(content.X >= placement.Panel.X + ToolDrawerLayout.Padding - .01f,
                "The content starts inside the sheet's own padding, right of its frame.");
            Check.True(content.X + content.Width <= placement.Panel.X + placement.Panel.Width + .01f,
                "The content never runs past the sheet it belongs to.");
            Check.True(content.Y + content.Height <= placement.Panel.Y + placement.Panel.Height + .01f,
                "The content never runs past the top of its sheet.");
            Check.True(content.X >= 0 && content.Y >= 0, "The content rectangle is a real screen rectangle.");
        }
    }

    /// <summary>
    /// A hosted page's controls must be drawn inside the sheet that asked for them. The renderer drew them one
    /// sheet-width to the right and one sheet-height up, because the element's own placement added the panel's
    /// screen position even though hosted controls are children of the panel object that already carries it: the
    /// whole page floated outside its frame while its clicks stayed where the controls should have been.
    /// </summary>
    internal static void HostedControlsStayInsideTheirSheet()
    {
        foreach (float preferredHeight in new[] { 240f, 780f, 880f, 2400f })
        {
            ToolDrawerLayout sheet = ToolDrawerLayout.MeasureHosted(preferredHeight);
            var content = new ToolInputRect(0f, 0f, sheet.InnerWidth, sheet.ContentHeight);
            var surface = new ToolPanelBuilder(content, ToolPanelPointer.None);
            // A page laid out the way a real one is: full-width rows, some split into cells, one separator. It
            // stops when the sheet is full, because running past the content rectangle is its own defect.
            surface.Row(54);
            surface.Status("status", "状态", surface.Row(30));
            surface.Separator("sep");
            int rows = 0;
            while (rows < 12 && surface.UsedHeight + 46f <= sheet.ContentHeight + .01f)
            {
                ToolInputRect row = surface.Row(46);
                surface.Button("b" + rows, "按钮", surface.Cell(row, 0, 3));
                surface.Button("c" + rows, "按钮", surface.Cell(row, 1, 3));
                surface.Button("d" + rows, "按钮", surface.Cell(row, 2, 3));
                rows++;
            }
            Check.True(rows > 0, $"Even the smallest sheet fits one row of controls; sheet {sheet.Height}.");
            Check.True(surface.UsedHeight <= sheet.ContentHeight + .01f,
                $"The test page must fit the sheet it measured; used {surface.UsedHeight:F0} of {sheet.ContentHeight:F0}.");
            foreach (ToolPanelElement element in surface.Elements)
            {
                if (element.Kind == ToolPanelElementKind.Area) continue;
                ToolInputRect drawn = ToolDrawerLayout.HostedElementBounds(element.Bounds);
                Check.True(ToolDrawerLayout.IsInsidePanel(drawn, sheet.PanelWidth, sheet.Height),
                    $"A hosted control must be drawn inside its own sheet; '{element.Id}' drew at {drawn}, "
                    + $"sheet {sheet.PanelWidth}x{sheet.Height}.");
                Check.True(drawn.X >= ToolDrawerLayout.Padding - .01f,
                    $"…and keep the sheet's left padding; '{element.Id}' drew at x={drawn.X:F1}.");
                Check.True(drawn.X + drawn.Width <= sheet.PanelWidth - ToolDrawerLayout.Padding + .01f,
                    $"…and its right padding; '{element.Id}' ended at {drawn.X + drawn.Width:F1} of {sheet.PanelWidth}.");
            }
            // The rectangle a page is handed in pixels is the same conversion, expressed in pixels.
            ToolInputRect pixels = ToolDrawerLayout.HostedElementPixels(
                new ToolInputRect(349f, 357f, sheet.PanelWidth, sheet.Height), 1f, content);
            Check.Equal(349f + ToolDrawerLayout.Padding, pixels.X, "The pixel rectangle starts at the sheet's padding.");
            Check.Equal(357f + ToolDrawerLayout.Padding, pixels.Y, "…on both axes.");
        }
    }

    /// <summary>
    /// A page that reads top-down asks for bands from the top. Reserving bands upward from the bottom — which is
    /// what <c>Row</c> does — draws a page's own reading order upside down, so the header ends up under the footer.
    /// </summary>
    internal static void TopDownBandsStackDownward()
    {
        var content = new ToolInputRect(0f, 0f, 560f, 400f);
        var surface = new ToolPanelBuilder(content, ToolPanelPointer.None);
        ToolInputRect first = surface.Band(40);
        ToolInputRect second = surface.Band(40);
        Check.Equal(content.Y + content.Height - 40f, first.Y, "The first band starts at the top of the sheet.");
        Check.Equal(first.Y - 40f, second.Y, "…and the band after it is below it.");
        Check.Equal(content.X, first.X, "A band spans the content width.");
        Check.Equal(content.Width, first.Width);
        Check.Equal(80f, surface.UsedFromTop, "The top cursor is how far down the page has come.");
        Check.True(surface.UsedHeight == 0f, "A page laid out from the top has reserved nothing from the bottom.");

        // A footer pinned to the bottom and sections stacked from the top may share one page, as long as the two
        // halves do not meet: that is what keeps a page from silently drawing over itself.
        var mixed = new ToolPanelBuilder(content, ToolPanelPointer.None);
        ToolInputRect header = mixed.Band(60);
        ToolInputRect footer = mixed.Row(50);
        Check.True(footer.Y + footer.Height < header.Y,
            $"A footer reserved from the bottom sits below a header reserved from the top; footer {footer}, header {header}.");
        Check.True(header.Y + header.Height <= content.Y + content.Height + .01f,
            "A band from the top never leaves the sheet.");
    }

    private static bool Overlaps(ToolInputRect a, ToolInputRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static bool Contains(ToolInputRect outer, ToolInputRect inner) =>
        inner.X >= outer.X - .01 && inner.Y >= outer.Y - .01
        && inner.X + inner.Width <= outer.X + outer.Width + .01
        && inner.Y + inner.Height <= outer.Y + outer.Height + .01;

    private static readonly ToolModule[] OneModule = { new("rukari.charactervoice", "人物配音支持", "wave") };
    private static readonly ToolModule[] FourModules =
    {
        new("rukari.charactervoice", "人物配音支持", "wave"),
        new("rukari.moreeffects", "更多的画面效果", "camera"),
        new("rukari.memorylobby", "记忆大厅支持", "card"),
        new("third.party.mod", "第三方模组", "")
    };
    private static readonly ToolModule[] SixteenModules = Enumerable.Range(0, 16)
        .Select(i => new ToolModule("mod" + i, "模组" + i, "")).ToArray();

    private static double Luminance(ToolColor c)
    {
        static double Channel(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    }
}
