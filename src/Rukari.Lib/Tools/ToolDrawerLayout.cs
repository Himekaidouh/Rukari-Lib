using System.Text;

namespace Rukari.Lib.Tools;

/// <summary>One module the toolbox can show. Every enabled mod contributes one button beside the big icon.</summary>
public sealed record ToolModule(string Id, string Title, string Icon);

/// <summary>
/// One entry of the open module, shown in the column that pops out beside the module buttons. Only a module that
/// publishes more than one entry gets that column: with a single entry its button opens the panel directly.
/// </summary>
public sealed record ToolEntry(string Id, string Title, string? IconId = null);

/// <summary>One big icon, the module buttons beside it, and the panel, all in physical screen pixels.</summary>
public readonly record struct ToolDrawerPlacement
{
    /// <summary>Captures one measurement of the left tool surfaces, all in physical screen pixels.</summary>
    public ToolDrawerPlacement(float scale, ToolInputRect master, ToolInputRect panel, IReadOnlyList<ToolInputRect> children, ToolInputRect catchAll)
    {
        Scale = scale; Master = master; Panel = panel; Children = children; CatchAll = catchAll;
        // A struct must assign every field before returning, and the entries column only exists for a multi-entry
        // module, so it starts absent and PlaceTree sets it through the initializer.
        EntryColumn = default;
        // Same for the card and its scrollbar: a module list that fits draws none of them.
        Card = default;
        CardHeader = default;
        CardCollapse = default;
        ScrollTrack = default;
        ScrollThumb = default;
        FirstVisibleRow = 0;
    }

    /// <summary>Scale the panel is drawn at.</summary>
    public float Scale { get; }
    /// <summary>The big icon, bottom-left of the group. It is the permanent way in and out.</summary>
    public ToolInputRect Master { get; }
    /// <summary>The open module's panel.</summary>
    public ToolInputRect Panel { get; }
    /// <summary>One rectangle per enabled module, the small buttons that pop out beside the big icon.</summary>
    public IReadOnlyList<ToolInputRect> Children { get; }
    /// <summary>
    /// The fail-closed guard band kept around the icon group. Published so a bug in the per-button rectangles can
    /// never let a left click fall through to the official editor, which treats it as "clicked empty space" and
    /// closes the node editor. It hugs the icon group instead of blanketing the whole left edge, because the
    /// official editor keeps a back button near the top-left corner and decorations near the bottom-left one. It
    /// widens to the rightmost column that is showing, and never reaches into the panel.
    /// </summary>
    public ToolInputRect CatchAll { get; }

    /// <summary>
    /// The entries column of a module that publishes several entries. It is empty for a single-entry module and
    /// for every module while the tree is closed, and <see cref="ToolDrawerLayout.PlaceLeft"/> produces none.
    /// </summary>
    public IReadOnlyList<ToolInputRect> Entries { get; init; } = Array.Empty<ToolInputRect>();

    /// <summary>The whole entries column, or <c>default</c> when it is not showing.</summary>
    public ToolInputRect EntryColumn { get; init; }

    /// <summary>
    /// Which module each entry of <see cref="Children"/> belongs to. <see cref="Children"/> holds only the rows
    /// the card can show at once, so the two lists are read together: with the column scrolled down, row 0 is no
    /// longer module 0, and a renderer that assumed otherwise would open the wrong mod.
    /// </summary>
    public IReadOnlyList<int> VisibleModules { get; init; } = Array.Empty<int>();

    /// <summary>Index of the first module the card shows. Zero while the whole list fits, which is the usual case.</summary>
    public int FirstVisibleRow { get; init; }

    /// <summary>
    /// The module card: the titled surface every module row lives in, or <c>default</c> when no module is
    /// registered. It is one plate rather than a bare stack of buttons, which is what gives the column a header
    /// to carry its own name and the control that collapses it.
    /// </summary>
    public ToolInputRect Card { get; init; }

    /// <summary>The card's title band, inside its top edge. Carries the card's name and its collapse control.</summary>
    public ToolInputRect CardHeader { get; init; }

    /// <summary>The card header's collapse control, or <c>default</c> when no card is drawn.</summary>
    public ToolInputRect CardCollapse { get; init; }

    /// <summary>The scrollbar track, or <c>default</c> while every registered module fits in the card.</summary>
    public ToolInputRect ScrollTrack { get; init; }

    /// <summary>The scrollbar thumb inside <see cref="ScrollTrack"/>, or <c>default</c> when there is no track.</summary>
    public ToolInputRect ScrollThumb { get; init; }
}

/// <summary>A measured drawer layout in canvas units, independent of native rendering.</summary>
public sealed record ToolDrawerLayout(
    float Height, int PageRows, int VisibleRows, int ActionRows,
    float SummaryHeight, float StatusHeight, bool Search, bool EmptyList, bool Pagination)
{
    /// <summary>Overall panel width.</summary>
    public const float Width = 620;
    /// <summary>Inset used on both sides of every full-width section.</summary>
    public const float Padding = 22;
    /// <summary>Usable width for summary, resources, and actions.</summary>
    public const float ContentWidth = Width - Padding * 2;

    /// <summary>
    /// Width of the sheet THIS measurement describes, in canvas units. It is <see cref="Width"/> for every page
    /// that describes itself with a snapshot, and the width a hosted page asked for through its sizing interface
    /// otherwise. Everything panel-local — the header, the close button and the content rectangle — is placed from
    /// this value, so one page can never inherit another page's width.
    /// </summary>
    public float PanelWidth { get; init; } = Width;

    /// <summary>Usable content width of this sheet: <see cref="PanelWidth"/> inside its own padding.</summary>
    public float InnerWidth => Math.Max(1f, PanelWidth - Padding * 2);

    /// <summary>
    /// Height of the content rectangle of this sheet: the sheet minus its padding and the header row the library
    /// draws for every page. A hosted page is told exactly this rectangle, and it is the sheet height only when the
    /// sheet was not shrunk to fit the window.
    /// </summary>
    public float ContentHeight => Math.Max(1f, Height - Padding * 2 - HeaderRowHeight);

    /// <summary>Height of the panel's header row, which carries the title and the close button.</summary>
    public const float HeaderHeight = 40;
    /// <summary>
    /// Distance from the top padding down to the first content row: the header row and the gap under it. It is
    /// named because the content rectangle a hosted page receives is derived from it, and a page that lays out its
    /// own editor against that rectangle must be able to measure the same sheet the renderer does.
    /// </summary>
    public const float HeaderRowHeight = 48;
    /// <summary>Side of the square close button. It is what keeps a header reachable at any scale.</summary>
    public const float CloseButtonSize = 40;
    /// <summary>The maximum number of resource rows displayed on one page.</summary>
    public const int MaximumRows = 8;
    /// <summary>Side of the big icon: the one permanent button, which opens every level below it.</summary>
    public const float RailButtonWidth = 96;
    /// <summary>Height of the big icon.</summary>
    public const float RailButtonHeight = 96;
    /// <summary>Narrowest module column, so a short mod name never produces a stubby button.</summary>
    public const float ModuleButtonMinimumWidth = 132;
    /// <summary>Widest module column, so one very long name cannot take over the screen.</summary>
    public const float ModuleButtonMaximumWidth = 300;
    /// <summary>Inset of a label from the plate's edge, in physical pixels.</summary>
    public const float ModuleButtonLabelInset = 16;
    /// <summary>Font size of a module button or entry row label, in physical pixels.</summary>
    public const int ModuleButtonFontSize = 18;
    /// <summary>
    /// Height the shared nine-sliced plate needs before Unity starts clamping its border, which would grow the
    /// corner into a semicircle. A module button and an entry row must both stay at or above it.
    /// </summary>
    public const float OfficialPlateMinimumHeight = 50;
    /// <summary>
    /// Height of one module button. At least <see cref="OfficialPlateMinimumHeight"/> so the plate keeps its own
    /// corner instead of being clamped.
    /// </summary>
    public const float ModuleButtonHeight = 52;
    /// <summary>Gap between two module buttons.</summary>
    public const float ModuleButtonGap = 6;
    /// <summary>
    /// Distance from a module row to the card's left edge. The rows are inset already, so the card itself is what
    /// draws the outline of the column.
    /// </summary>
    public const float ModuleCardPadding = 10;
    /// <summary>Space below the final module row, leaving room above the card's rounded edge and shadow.</summary>
    public const float ModuleCardBottomPadding = 24;
    /// <summary>
    /// Inset of a row from the card's right edge, wider than the left one because the scrollbar lives there. The
    /// gutter is reserved whether or not a scrollbar is showing, so registering one more mod can never shift the
    /// panel sideways.
    /// </summary>
    public const float ModuleCardRightPadding = 14;
    /// <summary>Height of the card's title band, which carries the card's name and its collapse control.</summary>
    public const float ModuleCardHeaderHeight = 40;
    /// <summary>Font size of the card's title, in physical pixels.</summary>
    public const int ModuleCardTitleFontSize = 17;
    /// <summary>Side of the card header's collapse control. Square, like the panel's close button.</summary>
    public const float ModuleCardCollapseSize = 30;
    /// <summary>Width of the module column's scrollbar track.</summary>
    public const float ModuleScrollbarWidth = 8;
    /// <summary>Distance from the scrollbar track to the card's own right edge.</summary>
    public const float ModuleScrollbarInset = 3;
    /// <summary>Shortest scrollbar thumb, so a very long module list still leaves something to grab.</summary>
    public const float ModuleScrollbarMinimumThumb = 36;
    /// <summary>
    /// How many module rows the card shows at once. It is a fixed number rather than "as many as fit": a column
    /// that grew with the window was a different size on every screen, and a card filling the whole left edge of a
    /// large window dwarfed the icon beside it. Five rows is the size the column is designed around, and every
    /// module past the fifth is reached by scrolling.
    ///
    /// It doubles as the bound the whole column is built on: the control pool and the published input region are
    /// sized from it, so the per-frame cost of the rail stops depending on how many mods are installed.
    /// </summary>
    public const int ModuleCardRowLimit = 5;
    /// <summary>Height one module row occupies inside the card, including the gap under it.</summary>
    public const float ModuleRowPitch = ModuleButtonHeight + ModuleButtonGap;
    /// <summary>Width of the entries column, which only exists for a module with several entries.</summary>
    public const float EntryColumnWidth = 176;
    /// <summary>
    /// Height of one entry row. Like a module button it must clear <see cref="OfficialPlateMinimumHeight"/>, since
    /// both wear the same official plate.
    /// </summary>
    public const float EntryRowHeight = 52;
    /// <summary>Vertical gap between two entry rows.</summary>
    public const float EntryRowGap = 6;
    /// <summary>Left margin of the big icon column, kept inside the reserved edge strip.</summary>
    public const float RailOffset = 26;
    /// <summary>Empty space between two columns of the tree.</summary>
    public const float RailPanelGap = 10;
    /// <summary>Left inset of the module column, measured from the screen edge.</summary>
    public const float LeftPanelOffset = RailOffset + RailButtonWidth + RailPanelGap;
    /// <summary>Distance the icon group keeps from the bottom-left screen corner.</summary>
    public const float MasterScreenMargin = 16;
    /// <summary>
    /// Height of the fail-closed guard band kept above and below the icon group. The rail sits in the middle of
    /// the left edge, where the official editor has controls near both corners, so the shield hugs the icons
    /// instead of blanketing the column: a shield wide enough to reach those corners would swallow them.
    /// </summary>
    public const float RailGuardMargin = 40;
    /// <summary>Panel width cap, so a very wide window adds margin rather than an unusably wide sheet.</summary>
    public const float MaximumPanelFraction = .34f;
    /// <summary>
    /// How much of the width it asked for a sheet may lose to <see cref="DockHalfWindowFraction"/> before that
    /// rule is dropped. On a window too narrow for the whole left band, keeping the sheet readable matters more
    /// than the split, and the drawer keeps the width it has always had there.
    /// </summary>
    public const float MinimumSheetFractionUnderDockCap = .85f;
    /// <summary>
    /// Half of the window. The dock — the icon, the module card, the entries column and the sheet — stays inside
    /// it, so the official editor always keeps the right half of the screen. The sheet is what absorbs the
    /// difference, because the columns are sized by their own content.
    /// </summary>
    public const float DockHalfWindowFraction = .5f;
    /// <summary>Height of a hosted page's separator, including the space above and below it.</summary>
    public const float SeparatorHeight = 13;
    /// <summary>Smallest sheet a hosted page may ask for, so a wrong value cannot collapse the panel.</summary>
    public const float MinimumHostedHeight = 240;
    /// <summary>Largest sheet a hosted page may ask for; the panel scale still shrinks it to the window.</summary>
    public const float MaximumHostedHeight = 2400;
    /// <summary>Narrowest sheet a hosted page may ask for. A narrower sheet has no room for its own controls.</summary>
    public const float MinimumHostedWidth = 320;
    /// <summary>Widest sheet a hosted page may ask for; a wider one would stop being a side panel.</summary>
    public const float MaximumHostedWidth = 1600;
    /// <summary>Largest icon scale; a bigger window must not inflate the icons.</summary>
    public const float MaximumRailScale = 1.15f;
    /// <summary>Smallest icon scale.</summary>
    public const float MinimumRailScale = .5f;

    /// <summary>
    /// Panel-local rectangle of the header title, measured from the panel's bottom-left corner with
    /// <paramref name="panelHeight"/> as the panel's drawn height. Both header rectangles stay inside the frame:
    /// the close button was once placed at `ContentWidth + 8`, which put its right edge four units past the
    /// panel's own width.
    /// </summary>
    public static ToolInputRect TitleBounds(float panelHeight) => TitleBounds(Width, panelHeight);

    /// <summary>The same header rectangle for a sheet of a given width, which a hosted page chooses for itself.</summary>
    public static ToolInputRect TitleBounds(float panelWidth, float panelHeight) => new(
        Padding + CloseButtonSize + 12,
        panelHeight - Padding - HeaderHeight,
        Math.Max(1f, panelWidth - (Padding + CloseButtonSize + 12) * 2),
        HeaderHeight);

    /// <summary>Panel-local rectangle of the close button, inset by the panel's own padding.</summary>
    public static ToolInputRect CloseBounds(float panelHeight) => CloseBounds(Width, panelHeight);

    /// <summary>The close button of a sheet of a given width: it hugs that sheet's own right padding.</summary>
    public static ToolInputRect CloseBounds(float panelWidth, float panelHeight) => new(
        panelWidth - Padding - CloseButtonSize,
        panelHeight - Padding - CloseButtonSize,
        CloseButtonSize,
        CloseButtonSize);

    /// <summary>
    /// True when a panel-local rectangle lies fully inside the drawn panel with at least
    /// <see cref="Padding"/> on the left and right. Every piece of panel chrome is expected to satisfy this.
    /// </summary>
    public static bool IsInsidePanel(ToolInputRect rect, float panelHeight) => IsInsidePanel(rect, Width, panelHeight);

    /// <summary>The same test for a sheet of a given width.</summary>
    public static bool IsInsidePanel(ToolInputRect rect, float panelWidth, float panelHeight) =>
        rect.X >= Padding - .01f
        && rect.X + rect.Width <= panelWidth - Padding + .01f
        && rect.Y >= -.01f
        && rect.Y + rect.Height <= panelHeight + .01f;

    /// <summary>
    /// Index of the module button that must look pressed, or -1 when none of them may. The rail icon opens the
    /// column with nothing selected, so "no module is open" has to survive as -1 all the way to the drawing: a
    /// clamp to the first module would light a button the user never pressed, and the next press on that button
    /// would then read as a close — the exact press that looked like it did nothing.
    /// </summary>
    public static int HighlightedModuleIndex(int openModuleIndex, int moduleCount) =>
        moduleCount > 0 && openModuleIndex >= 0 && openModuleIndex < moduleCount ? openModuleIndex : -1;

    /// <summary>
    /// Places a panel-local rectangle — canvas units measured from the sheet's bottom-left corner — into physical
    /// screen pixels. Both the panel's own origin and the drawn scale apply, because the sheet may have been shrunk
    /// to fit the window. A hosted page is handed this result instead of deriving it, so the page and the renderer
    /// cannot end up disagreeing about where the content really is.
    /// </summary>
    public static ToolInputRect ToScreenPixels(ToolInputRect panel, float scale, ToolInputRect panelLocal) => new(
        panel.X + panelLocal.X * scale,
        panel.Y + panelLocal.Y * scale,
        panelLocal.Width * scale,
        panelLocal.Height * scale);

    /// <summary>
    /// Measures a hosted page, whose content height the mod states instead of deriving it from a snapshot. The
    /// value is clamped so a wrong number cannot collapse or overflow the sheet, and the ordinary panel scale
    /// still shrinks the whole thing to fit the window.
    ///
    /// The height and width are canvas units — the size the page wants on a 1:1 screen, which is what canvas units
    /// mean while the sheet is drawn at its natural size. A page whose own coordinates are authored against the
    /// game's reference resolution converts them once with its own scale rather than assuming the sheet stays 1:1:
    /// a small window shrinks the sheet, and the page reads the rectangle it actually got from
    /// <see cref="IToolPanelSurface.BoundsInPixels"/>.
    /// </summary>
    public static ToolDrawerLayout MeasureHosted(float preferredHeight) => MeasureHosted(preferredHeight, 0f);

    /// <summary>
    /// The same measurement for a page that also states how wide a sheet it needs. A page with no opinion — one
    /// that only implements <see cref="IToolPanelContent"/> — passes zero and gets the shared default sheet.
    /// </summary>
    public static ToolDrawerLayout MeasureHosted(float preferredHeight, float preferredWidth) => new(
        Math.Clamp(float.IsFinite(preferredHeight) ? preferredHeight : MinimumHostedHeight,
            MinimumHostedHeight, MaximumHostedHeight),
        0, 0, 0, 0f, 0f, false, false, false)
    {
        PanelWidth = ClampHostedWidth(preferredWidth)
    };

    /// <summary>
    /// Sheet width for a hosted page: its own request when that is a usable number, and the shared default sheet
    /// otherwise. Zero, a negative value and a non-finite value all mean "no opinion", which is exactly what a
    /// page that does not implement <c>IToolPanelSizing</c> reports.
    /// </summary>
    public static float ClampHostedWidth(float preferredWidth) =>
        float.IsFinite(preferredWidth) && preferredWidth > 0
            ? Math.Clamp(preferredWidth, MinimumHostedWidth, MaximumHostedWidth)
            : Width;

    /// <summary>
    /// Number of lines a text block needs at the given font size and width, capped at
    /// <paramref name="maximum"/>. Exposed because a hosted page has to size its own rectangles and the width of
    /// a CJK rune is not the width of an ASCII one.
    /// </summary>
    public static int CountLines(string? value, int fontSize, float width, int maximum = 64)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        float capacity = Math.Max(1f, width) / Math.Max(1, fontSize);
        int limit = Math.Max(1, maximum);
        int lines = 1;
        float used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value == '\r') continue;
            if (rune.Value == '\n') { lines++; used = 0; }
            else
            {
                float next = EmWidth(rune);
                if (used + next > capacity) { lines++; used = 0; }
                used += next;
            }
            if (lines >= limit) return limit;
        }
        return lines;
    }

    /// <summary>Height a text block needs at the given font size and width, using the shared line height.</summary>
    public static float MeasureTextHeight(string? value, int fontSize, float width) =>
        CountLines(value, fontSize, width) * LineHeight(fontSize);

    /// <summary>Line height used for a font size, matching what the panel actually draws.</summary>
    public static float LineHeight(int fontSize) => Math.Max(1, fontSize) + 7;

    /// <summary>Measures only the sections actually shown, including a short empty-list message.</summary>
    public static ToolDrawerLayout Measure(int modulePageCount, int itemCount, int actionCount,
        bool hasList, bool allowSearch, string? summary, string? status)
    {
        // A module with a single page needs no page switcher; only extra pages add one compact row.
        int pageRows = Math.Clamp(modulePageCount, 0, 16) > 1 ? 1 : 0;
        int rows = hasList ? Math.Clamp(itemCount, 0, MaximumRows) : 0;
        int actionRows = (Math.Clamp(actionCount, 0, 8) + 1) / 2;
        float summaryHeight = EstimateLines(summary, 18, ContentWidth, 4) * 25;
        float statusHeight = EstimateLines(status, 17, ContentWidth, 4) * 24;
        bool search = hasList && allowSearch;
        bool empty = hasList && rows == 0;
        bool pagination = hasList && rows > 0;
        float height = Padding * 2 + 48 + pageRows * 44 + 10
            + (summaryHeight > 0 ? summaryHeight + 10 : 0)
            + (search ? 46 : 0) + rows * 42 + (empty ? 38 : 0)
            + (pagination ? 44 : 0) + actionRows * 45
            + (statusHeight > 0 ? statusHeight + 10 : 0);
        return new(height, pageRows, rows, actionRows, summaryHeight, statusHeight, search, empty, pagination);
    }

    /// <summary>
    /// Scale that follows the window, never inflates past a comfortable size and never shrinks past
    /// <see cref="MinimumRailScale"/>.
    ///
    /// It deliberately does NOT depend on how many mods are installed. It used to shrink the whole drawer by the
    /// module count so that a long column still fitted the window, which made every mod that registers itself
    /// smaller than the last one. The card is bounded by <see cref="ModuleCardRowLimit"/> and scrolls
    /// instead, so the only thing left to follow is the window itself.
    /// </summary>
    public static float RailScaleFor(int pixelWidth, int pixelHeight) => Math.Max(.02f,
        Math.Clamp(pixelHeight / 900f, MinimumRailScale, MaximumRailScale));

    /// <summary>Physical left edge shared by the icon column and every feature panel.</summary>
    public static float LeftPanelEdgePixels(int pixelWidth, int pixelHeight) =>
        RailOffset * RailScaleFor(pixelWidth, pixelHeight);

    /// <summary>
    /// Height of the module card showing this many rows: its title band, its padding, and the rows themselves.
    /// Zero rows is still a card, because the title band is where the column's own name and its collapse control
    /// live.
    /// </summary>
    public static float ModuleCardHeight(int visibleRows)
    {
        int rows = Math.Clamp(visibleRows, 0, ModuleCardRowLimit);
        float band = rows == 0 ? 0f : rows * ModuleButtonHeight + (rows - 1) * ModuleButtonGap;
        return ModuleCardHeaderHeight + ModuleCardPadding + ModuleCardBottomPadding + band;
    }

    /// <summary>
    /// How many rows a screen of this height can hold. The card is normally capped by
    /// <see cref="ModuleCardRowLimit"/> instead; this is the safety net for a window so short that even five rows
    /// would not fit between its corners, where the card gives up rows rather than covering the official controls
    /// near them. The clearance it keeps is the same one the guard band needs.
    /// </summary>
    public static int ModuleRowsThatFitOnScreen(int pixelHeight)
    {
        float room = pixelHeight - 2 * (MasterScreenMargin + RailGuardMargin)
            - ModuleCardHeaderHeight - ModuleCardPadding - ModuleCardBottomPadding + ModuleButtonGap;
        return Math.Clamp((int)MathF.Floor(room / ModuleRowPitch), 1, ModuleCardRowLimit);
    }

    /// <summary>
    /// Rows the card really shows: the modules there are, capped by the card's own five-row limit and by what a
    /// very short window can hold. On every ordinary screen this is simply "five", whatever the window size and
    /// however many mods are installed, which is what keeps the column the same shape everywhere.
    /// </summary>
    public static int VisibleModuleRows(int pixelHeight, int moduleCount) =>
        Math.Min(Math.Max(0, moduleCount), Math.Min(ModuleCardRowLimit, ModuleRowsThatFitOnScreen(pixelHeight)));

    /// <summary>Whether a count of modules needs a scrollbar on a screen of this height.</summary>
    public static bool ModuleColumnScrolls(int pixelHeight, int moduleCount) =>
        ToolScroll.Needed(moduleCount, VisibleModuleRows(pixelHeight, moduleCount));

    /// <summary>
    /// Height of the whole icon group: the big icon and the module card that pops out beside it. The two sit side
    /// by side, so the group is as tall as the taller of them.
    /// </summary>
    public static float RailGroupHeight(int visibleRows) => Math.Max(RailButtonHeight, ModuleCardHeight(visibleRows));

    /// <summary>
    /// Bottom edge of the icon group, in physical pixels. The group is centred on the left edge because the
    /// official editor keeps controls near BOTH left corners (a back button top-left, decorations bottom-left),
    /// so the middle of the left edge is the one place the rail cannot cover anything.
    ///
    /// Rail rectangles are already physical pixels, so this takes no scale: the drawer divides by the canvas
    /// scale when it draws them. The row count is what the card actually shows, never the number of installed
    /// mods: a longer list scrolls inside the same card instead of growing the group off the screen.
    /// </summary>
    public static float RailGroupBottom(int pixelHeight, int visibleRows)
    {
        float group = RailGroupHeight(visibleRows);
        return Math.Max(MasterScreenMargin, (pixelHeight - group) / 2f);
    }

    /// <summary>
    /// Vertical centre of the big icon: centred inside the group, beside the module card. On a window too short
    /// for the whole group it snaps back inside the bottom margin, because the big icon is the only way in and out
    /// and must never be pushed off screen by a long module list.
    /// </summary>
    public static float MasterBottom(int pixelHeight, int visibleRows)
    {
        float y = RailGroupBottom(pixelHeight, visibleRows)
            + (RailGroupHeight(visibleRows) - RailButtonHeight) / 2f;
        float limit = Math.Max(MasterScreenMargin, pixelHeight - RailButtonHeight - MasterScreenMargin);
        return Math.Clamp(y, MasterScreenMargin, limit);
    }

    /// <summary>
    /// The bottom-left big icon, in physical pixels. It is the one permanent control and the only large target;
    /// every level below it pops out to its right. The module count is turned into the rows the card will really
    /// show, so the caller passes what it has rather than what it thinks fits.
    ///
    /// Y is measured from the BOTTOM edge and grows upward, which is what both Unity screen coordinates and the
    /// bottom-anchored (anchorMin/pivot = 0,0) RectTransforms the drawer creates actually use. Computing a
    /// bottom edge as <c>pixelHeight - margin - height</c> instead put the whole column at the top of the screen.
    /// </summary>
    public static ToolInputRect MasterBounds(int pixelWidth, int pixelHeight, int moduleCount)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return default;
        return new ToolInputRect(
            IconColumnX,
            MasterBottom(pixelHeight, VisibleModuleRows(pixelHeight, moduleCount)),
            RailButtonWidth,
            RailButtonHeight);
    }

    /// <summary>Physical left edge of the big icon column.</summary>
    public static float IconColumnX => RailOffset;

    /// <summary>
    /// Physical left edge of the module card: the surface that pops out beside the big icon once it is pressed.
    /// This is what <see cref="LeftPanelOffset"/> used to be, so a caller that shows no modules sees no change.
    /// </summary>
    public static float ModuleColumnX => RailOffset + RailButtonWidth + RailPanelGap;

    /// <summary>
    /// Width of the module card for a measured row width: the row plus the card's own padding on both sides. The
    /// right padding is the wider one because the scrollbar's gutter is reserved there whether or not it is
    /// showing, which is what keeps the panel from moving when a mod is added.
    /// </summary>
    public static float ModuleCardWidth(float rowWidth) =>
        ModuleCardPadding + Math.Max(0f, rowWidth) + ModuleCardRightPadding;

    /// <summary>Physical right edge of the module card for a measured row width.</summary>
    public static float ModuleColumnRight(float columnWidth) => ModuleColumnX + ModuleCardWidth(columnWidth);

    /// <summary>
    /// Physical left edge of the entries column, which only exists for a module that publishes more than one
    /// entry: one entry is opened by its module button directly, so it needs no column of its own.
    /// </summary>
    public static float EntryColumnX(float columnWidth) => ModuleColumnRight(columnWidth) + RailPanelGap;

    /// <summary>Physical right edge of the entries column.</summary>
    public static float EntryColumnRight(float columnWidth) => EntryColumnX(columnWidth) + EntryColumnWidth;

    /// <summary>
    /// Physical left edge of the panel. It sits right of the entries column when there is one, and takes that
    /// column's place when there is not, so no gap is ever left behind. It does not move while one module is open,
    /// so pressing a leaf cannot make the sheet jump sideways.
    /// </summary>
    public static float PanelXFor(float columnWidth, bool showEntries) =>
        (showEntries ? EntryColumnRight(columnWidth) : ModuleColumnRight(columnWidth)) + RailPanelGap;

    /// <summary>
    /// The module column's width for a set of modules: wide enough for the longest title at the shared font size,
    /// clamped so a very short or a very long name still produces a usable button. Every button in the column uses
    /// this one width, so their labels line up on the same left edge.
    /// </summary>
    public static float ColumnWidthFor(IReadOnlyList<ToolModule>? modules) => Math.Clamp(
        WidthNeededFor(LongestTitle(modules)),
        ModuleButtonMinimumWidth,
        ModuleButtonMaximumWidth);

    /// <summary>
    /// The same measurement for the entries of a module. The entries column shares the module column's width, so
    /// it has to be wide enough for whichever of the two has the longer text. It has its own name rather than an
    /// overload because two interface-typed overloads make a bare <c>null</c> ambiguous at the call site.
    /// </summary>
    public static float EntryColumnWidthFor(IReadOnlyList<ToolEntry>? entries)
    {
        string longest = "";
        if (entries is not null)
        {
            foreach (ToolEntry entry in entries)
            {
                if (WidthNeededFor(entry.Title) > WidthNeededFor(longest)) longest = entry.Title ?? "";
            }
        }
        return Math.Clamp(WidthNeededFor(longest), ModuleButtonMinimumWidth, ModuleButtonMaximumWidth);
    }

    /// <summary>Physical width a label needs, so a caller can size a plate instead of truncating the text.</summary>
    public static float MeasureLabelWidth(string? value, int fontSize) =>
        Math.Max(.01f, (value ?? "").EnumerateRunes().Sum(EmWidth) * Math.Max(1, fontSize));

    /// <summary>Width a plate needs for a label: the text plus its inset on both sides, plus the outline.</summary>
    public static float WidthNeededFor(string? value) =>
        MeasureLabelWidth(value, ModuleButtonFontSize) + 2 * ModuleButtonLabelInset + 12;

    private static string LongestTitle(IReadOnlyList<ToolModule>? modules)
    {
        string longest = "";
        if (modules is null) return longest;
        foreach (ToolModule module in modules)
        {
            if (WidthNeededFor(module.Title) > WidthNeededFor(longest)) longest = module.Title ?? "";
        }
        return longest;
    }

    /// <summary>
    /// The fail-closed guard band around the icon group, in bottom-up physical pixels: from
    /// <paramref name="bottom"/> to <paramref name="top"/>, kept at the rail's x extent so it never reaches into
    /// the panel. A left click that lands here is swallowed instead of reaching the official editor, which would
    /// treat it as "clicked empty space" and close the node editor. It deliberately does NOT span the whole
    /// column: the official editor has a back button near the top-left corner and decorations near the
    /// bottom-left one, and a shield that reached them would swallow those controls too.
    ///
    /// Its left edge is the icon column itself (<see cref="IconColumnX"/>), not the scaled panel edge: the icons
    /// are drawn in unscaled physical pixels, so a band derived from the rail scale started a few pixels inside
    /// the icons and left a narrow strip beside them where a click could still fall through.
    /// </summary>
    public static ToolInputRect CatchAllFor(
        int pixelWidth,
        int pixelHeight,
        float bottom,
        float top,
        float width)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return default;
        float lower = Math.Clamp(bottom, 0f, pixelHeight);
        float upper = Math.Clamp(top, lower, pixelHeight);
        return new ToolInputRect(
            IconColumnX,
            lower,
            width,
            Math.Max(1f, upper - lower));
    }

    /// <summary>
    /// Panel scale. The sheet may overlap the official workspace, which the user explicitly allowed, so it is
    /// bounded only by the icon scale and its own height. Width is capped at <see cref="Width"/> instead.
    /// </summary>
    public float ScaleFor(int pixelWidth, int pixelHeight)
    {
        float rail = RailScaleFor(pixelWidth, pixelHeight);
        return Math.Max(.01f, Math.Min(rail, pixelHeight / (Height + 40)));
    }

    /// <summary>
    /// Places the master icon, the child stack and the panel. The master never moves; opening the drawer only
    /// reveals the children and the panel. Nothing is ever pushed off screen by content height.
    ///
    /// This overload shows no middle column, so existing callers keep the layout they had.
    /// </summary>
    public ToolDrawerPlacement PlaceLeft(int pixelWidth, int pixelHeight, IReadOnlyList<ToolModule> modules, int selectedIndex, bool expanded = true) =>
        PlaceTree(pixelWidth, pixelHeight, modules, selectedIndex, Array.Empty<ToolEntry>(), -1, expanded);

    /// <summary>
    /// Places the whole tree: the big icon, the module card that pops out beside it, the entries column of a
    /// multi-entry module, and the panel. Every level pops out to the RIGHT of the previous one, and each level
    /// only exists when the level above it is open, so collapsing walks back one column at a time.
    /// Pass an empty <paramref name="entries"/> to leave the entries column out entirely, which is what a module
    /// with a single entry does: its button opens the panel directly, so no redundant column is drawn.
    ///
    /// <paramref name="treeOpen"/> means the tree is open past the icon, which is depth 1 (the module card) or 2 (a
    /// panel as well). It is deliberately not "the panel is showing": the panel's scale is decided as soon as the
    /// user opens a module, so pressing a button does not make the sheet resize under the pointer.
    ///
    /// <paramref name="firstRow"/> is the module the card starts at. It is clamped here, so a caller that keeps a
    /// scroll position across a resize cannot hand in one the card cannot show; the value the placement really
    /// used comes back as <see cref="ToolDrawerPlacement.FirstVisibleRow"/>.
    /// </summary>
    public ToolDrawerPlacement PlaceTree(
        int pixelWidth,
        int pixelHeight,
        IReadOnlyList<ToolModule> modules,
        int selectedIndex,
        IReadOnlyList<ToolEntry> entries,
        int openEntryIndex,
        bool treeOpen = true,
        int firstRow = 0)
    {
        int count = Math.Max(0, modules?.Count ?? 0);
        int index = count == 0 ? 0 : Math.Clamp(selectedIndex, 0, count - 1);
        int entryCount = entries?.Count ?? 0;
        bool showEntries = entryCount > 0 && (treeOpen || openEntryIndex >= 0);
        // The rows are as wide as the longest mod name needs, so a short name and a long one both get a button
        // that fits its label without truncating it, and every row shares one left edge. When the entries column
        // is showing, it shares that width and the wider of the two wins.
        float columnWidth = Math.Max(ColumnWidthFor(modules), showEntries ? EntryColumnWidthFor(entries) : 0f);
        float scale = RailScaleFor(pixelWidth, pixelHeight);
        float wanted = treeOpen ? ScaleFor(pixelWidth, pixelHeight) : scale;
        float panelEdge = PanelXFor(columnWidth, showEntries);
        // The sheet is as wide as this page asked for, and it still never draws wider than it asked: a hosted
        // page that wants to line up with a native editor of its own gets its own width instead of the shared
        // 620, while a snapshot page keeps the sheet it has always had.
        float sheet = Math.Max(1f, PanelWidth);
        float panelWidth = Math.Min(sheet, Math.Min(sheet * wanted, Math.Max(1f, pixelWidth - panelEdge)));
        // The dock — icons, card, entries column and sheet — stays inside the left half of the window, so the
        // official editor always keeps the right half. Growing the card therefore comes out of the sheet's own
        // width, by a couple of percent, instead of pushing the sheet into the workspace. On a window too narrow
        // for the whole dock the rule is dropped rather than shrinking the sheet to an unreadable strip.
        float halfWindow = Math.Max(1f, pixelWidth * DockHalfWindowFraction - panelEdge);
        if (panelWidth > halfWindow && halfWindow >= panelWidth * MinimumSheetFractionUnderDockCap)
            panelWidth = halfWindow;
        // The sheet never grows past its own width, so the drawn scale follows the width that actually fits.
        // Without this the content was laid out for the wider sheet while the stretched panel held less, which is
        // how a wide window produced a wrong-sized panel.
        float panelScale = treeOpen ? panelWidth / sheet : scale;
        float panelHeight = Height * panelScale;
        float panelY = Math.Max(12f, pixelHeight / 2f - panelHeight / 2f);
        var panel = new ToolInputRect(panelEdge, panelY, panelWidth, panelHeight);

        // The icon group is laid out in its own fixed units, centred on the left edge, so the big icon never moves
        // while the tree opens and closes. Y grows upward from the bottom edge, matching the bottom-anchored
        // RectTransforms this placement is drawn into. Only the rows the card can show count towards its height:
        // a longer module list scrolls inside this same box.
        int visibleRows = VisibleModuleRows(pixelHeight, count);
        int firstVisible = ToolScroll.ClampFirst(firstRow, count, visibleRows);
        float groupBottom = RailGroupBottom(pixelHeight, visibleRows);
        var master = new ToolInputRect(IconColumnX, MasterBottom(pixelHeight, visibleRows), RailButtonWidth, RailButtonHeight);

        // The card is placed whether or not the tree is open, because the fail-closed guard band below has to
        // cover the area the first press will reveal.
        float cardWidth = ModuleCardWidth(columnWidth);
        float cardHeight = ModuleCardHeight(visibleRows);
        var card = count == 0 ? default : new ToolInputRect(ModuleColumnX, groupBottom, cardWidth, cardHeight);
        ToolInputRect header = default;
        ToolInputRect collapse = default;
        ToolInputRect track = default;
        ToolInputRect thumb = default;
        var children = new List<ToolInputRect>(visibleRows);
        var visible = new List<int>(visibleRows);
        if (count != 0)
        {
            // Rows read top down from under the header, which is what makes the card a list rather than a stack
            // growing out of the icon. The header band is what caps them.
            float band = visibleRows == 0 ? 0f : visibleRows * ModuleButtonHeight + (visibleRows - 1) * ModuleButtonGap;
            float bandTop = groupBottom + ModuleCardBottomPadding + band;
            for (int i = 0; i < visibleRows; i++)
            {
                int moduleIndex = firstVisible + i;
                if (moduleIndex >= count) break;
                children.Add(new ToolInputRect(
                    ModuleColumnX + ModuleCardPadding,
                    bandTop - ModuleButtonHeight - i * ModuleRowPitch,
                    columnWidth,
                    ModuleButtonHeight));
                visible.Add(moduleIndex);
            }
            float headerTop = groupBottom + cardHeight - ModuleCardHeaderHeight;
            float headerWidth = Math.Max(1f, cardWidth - ModuleCardPadding - ModuleCardRightPadding);
            header = new ToolInputRect(ModuleColumnX + ModuleCardPadding, headerTop, headerWidth, ModuleCardHeaderHeight);
            float collapseSize = Math.Min(ModuleCardCollapseSize, ModuleCardHeaderHeight);
            collapse = new ToolInputRect(
                header.X + header.Width - collapseSize,
                header.Y + (ModuleCardHeaderHeight - collapseSize) / 2f,
                collapseSize,
                collapseSize);
            if (children.Count != 0 && ToolScroll.Needed(count, visibleRows))
            {
                float trackBottom = groupBottom + ModuleCardBottomPadding;
                float rowsHeight = band;
                track = new ToolInputRect(
                    ModuleColumnX + cardWidth - ModuleCardRightPadding + ModuleScrollbarInset,
                    trackBottom,
                    ModuleScrollbarWidth,
                    Math.Max(1f, rowsHeight));
                thumb = ToolScroll.Thumb(track, firstVisible, count, visibleRows, ModuleScrollbarMinimumThumb);
            }
        }

        // The entries column mirrors the card: it starts on the group's own bottom line, grows upward, and is as
        // wide as a module row so the two read as one tree.
        var entryRects = new List<ToolInputRect>();
        ToolInputRect entryColumn = default;
        if (showEntries)
        {
            float entryX = EntryColumnX(columnWidth);
            float top = groupBottom;
            float band = entryCount * EntryRowHeight + (entryCount - 1) * EntryRowGap;
            entryColumn = new ToolInputRect(entryX, top, columnWidth, Math.Max(EntryRowHeight, band));
            for (int i = 0; i < entryCount; i++)
            {
                float y = top + band - EntryRowHeight - i * (EntryRowHeight + EntryRowGap);
                if (y < 0) break;
                entryRects.Add(new ToolInputRect(entryX, y, columnWidth, EntryRowHeight));
            }
        }

        // The catch-all hugs the group: everything the drawer draws, plus one guard margin on each side. It grows
        // with the column, so a mod that registers another module widens its own protection without any per-mod
        // configuration, and it never reaches the official controls near the screen corners. Its top is the
        // TALLEST thing drawn, which for the card is its header rather than its last row: a band that stopped at
        // the rows would leave the header uncovered. It widens to the rightmost column that is showing, so a wrong
        // per-row rectangle cannot let a click fall through into the official workspace either.
        float stackTop = master.Y + master.Height;
        if (card.IsValid) stackTop = Math.Max(stackTop, card.Y + card.Height);
        if (entryColumn.IsValid) stackTop = Math.Max(stackTop, entryColumn.Y + entryColumn.Height);
        float guardWidth = (entryColumn.IsValid ? EntryColumnRight(columnWidth) : ModuleColumnRight(columnWidth)) - IconColumnX;
        var catchAll = CatchAllFor(
            pixelWidth,
            pixelHeight,
            Math.Max(0f, groupBottom - RailGuardMargin),
            Math.Min(pixelHeight, stackTop + RailGuardMargin),
            guardWidth);
        return new ToolDrawerPlacement(panelScale, master, panel, children, catchAll)
        {
            Entries = entryRects,
            EntryColumn = entryColumn,
            VisibleModules = visible,
            FirstVisibleRow = firstVisible,
            Card = card,
            CardHeader = header,
            CardCollapse = collapse,
            ScrollTrack = track,
            ScrollThumb = thumb
        };
    }

    /// <summary>
    /// Where one hosted page's element is drawn, in the sheet's own coordinates: the element's content rectangle,
    /// moved to where the content starts inside the sheet.
    ///
    /// It deliberately does NOT include the sheet's position on screen. A hosted control is a child of the panel
    /// object, so that object's own transform already applies it; adding it here a second time drew every hosted
    /// page one sheet-width to the right and one sheet-height above the frame that was meant to contain it, while
    /// the hit test — which subtracts only the panel origin — went on working where the controls should have been.
    /// </summary>
    public static ToolInputRect HostedElementBounds(ToolInputRect contentBounds) => new(
        contentBounds.X + Padding,
        contentBounds.Y + Padding,
        contentBounds.Width,
        contentBounds.Height);

    /// <summary>
    /// The same conversion into physical pixels, for a page that places its own canvas. <paramref name="panel"/> is
    /// the sheet in physical pixels and <paramref name="scale"/> is the sheet's drawn scale.
    /// </summary>
    public static ToolInputRect HostedElementPixels(ToolInputRect panel, float scale, ToolInputRect contentBounds) =>
        ToScreenPixels(panel, scale, HostedElementBounds(contentBounds));

    /// <summary>Fits a button label on one line, preserving complete Unicode characters.</summary>
    public static string FitSingleLine(string? value, int fontSize, float width)
    {
        string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        float capacity = Math.Max(1, width / Math.Max(1, fontSize));
        if (text.EnumerateRunes().Sum(EmWidth) <= capacity) return text;
        var result = new StringBuilder();
        float used = 1.1f; // Reserve room for the ellipsis in the same font as CJK labels.
        foreach (Rune rune in text.EnumerateRunes())
        {
            used += EmWidth(rune);
            if (used > capacity) break;
            result.Append(rune.ToString());
        }
        return result + "…";
    }

    private static int EstimateLines(string? value, int fontSize, float width, int maximum) =>
        CountLines(value, fontSize, width, maximum);

    private static float EmWidth(Rune rune) => rune.Value switch
    {
        'W' or 'M' or 'w' or 'm' => 1f,
        < 128 => .75f,
        _ => 1.1f
    };
}
