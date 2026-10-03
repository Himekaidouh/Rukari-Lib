extern alias unitycore;
extern alias unitytext;
extern alias unityui;
extern alias unityuimodule;

using Rukari.Lib.Tools;
using Canvas = unityuimodule::UnityEngine.Canvas;
using CanvasScaler = unityui::UnityEngine.UI.CanvasScaler;
using Color = unitycore::UnityEngine.Color;
using Font = unitytext::UnityEngine.Font;
using FontStyle = unitytext::UnityEngine.FontStyle;
using GameObject = unitycore::UnityEngine.GameObject;
using HorizontalWrapMode = unitytext::UnityEngine.HorizontalWrapMode;
using Image = unityui::UnityEngine.UI.Image;
using KeyCode = unitycore::UnityEngine.KeyCode;
using Object = unitycore::UnityEngine.Object;
using RectTransform = unitycore::UnityEngine.RectTransform;
using RenderMode = unityuimodule::UnityEngine.RenderMode;
using Screen = unitycore::UnityEngine.Screen;
using Sprite = unitycore::UnityEngine.Sprite;
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;
using Transform = unitycore::UnityEngine.Transform;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector3 = unitycore::UnityEngine.Vector3;
using VerticalWrapMode = unitytext::UnityEngine.VerticalWrapMode;

namespace Rukari.Lib.Runtime.Tools;

internal sealed class ToolboxDrawer : IDisposable
{
    private const float Width = ToolDrawerLayout.Width;
    private const float ContentX = ToolDrawerLayout.Padding;
    private const float ContentWidth = ToolDrawerLayout.ContentWidth;
    private const int MaxRows = ToolDrawerLayout.MaximumRows;
    private readonly ToolInputService _input;
    private readonly Action<string> _log;
    private string _lastLayoutLog = "";
    private int _iconLogCount;
    private readonly IToolInputRegion _region;
    private readonly GameObject _canvasObject;
    private readonly Canvas _canvas;
    private readonly CanvasScaler _scaler;
    private readonly GameObject _panelObject;
    private readonly RectTransform _panelRect;
    private readonly Font _font;
    private readonly Text _title;
    private readonly OfficialPopupFrame _panelFrame;
    private readonly OfficialPopupFrame _cardFrame;
    private readonly (RectTransform Rect, Image Image) _closeGlyph;
    private readonly Text _summary;
    private readonly Text _status;
    private readonly Text _pageInfo;
    private readonly Control _master;
    private readonly Control _close;
    private readonly Control _search;
    private readonly Control _searchClear;
    private readonly Control _previous;
    private readonly Control _next;
    /// <summary>
    /// The module rows, one control per row the card can show rather than one per registered mod. This is the
    /// bound that keeps the rail's per-frame cost independent of how many mods are installed: the card shows
    /// <see cref="ToolDrawerLayout.ModuleCardRowLimit"/> rows and the rest scroll, so the pool never grows.
    /// </summary>
    private readonly Control[] _rail = new Control[ToolDrawerLayout.ModuleCardRowLimit];
    private readonly Control[] _entries = new Control[16];
    private readonly Control[] _rows = new Control[MaxRows];
    private readonly Control[] _actions = new Control[8];
    /// <summary>The module card: its plate, its title band, the title itself, and the control that collapses it.</summary>
    private readonly Control _card;
    private readonly Text _cardTitle;
    private readonly Control _cardCollapse;
    /// <summary>The card's scrollbar. Both are hidden while every registered module fits.</summary>
    private readonly Control _scrollTrack;
    private readonly Control _scrollThumb;
    /// <summary>Index of the first module row the card shows; the whole column fits for most installations.</summary>
    private int _moduleFirstRow;
    /// <summary>True while the user is dragging the scrollbar thumb.</summary>
    private bool _moduleScrollDrag;
    /// <summary>Distance from the pointer to the thumb's own bottom edge, kept constant during a drag.</summary>
    private float _moduleScrollGrab;
    /// <summary>
    /// Pool for a hosted page's controls. Created on first use and only grown, because most sessions never open a
    /// page that draws itself and each control is a native game object.
    /// </summary>
    private Control[] _hosted = Array.Empty<Control>();
    /// <summary>Last frame's hosted layout, used to resolve the press that starts this frame.</summary>
    private ToolPanelElement[] _hostedLast = Array.Empty<ToolPanelElement>();
    /// <summary>
    /// Which hosted page owns the visible panel, so its show and hide callbacks fire once per transition no
    /// matter which of the several paths closes the panel.
    /// </summary>
    private readonly ToolPanelSession _hostedSession = new();
    /// <summary>Usable content width of this frame's sheet, which a hosted page may have chosen for itself.</summary>
    private float _contentWidth = ContentWidth;
    /// <summary>Width and height of this frame's sheet in canvas units, logged so a screenshot can be matched.</summary>
    private float _sheetWidth = ToolDrawerLayout.Width;
    private float _sheetHeight;
    /// <summary>The hosted control the current press belongs to, or null when the press is not a page's.</summary>
    private string? _hostedPressed;
    private int _hostedDrawn;
    /// <summary>
    /// The field a hosted page focused this frame, or null when the keyboard is nobody's. The page owns the focus
    /// flag because it is the only one that knows which of its fields is being edited; the drawer owns the
    /// keyboard, so this is what the two have to agree on.
    /// </summary>
    private string? _hostedField;
    /// <summary>This frame's keystrokes for the focused field, collected after the page drew and read next frame.</summary>
    private string _hostedTyped = "";
    private bool _hostedBackspace;
    private bool _hostedSubmit;
    private bool _hostedCancel;
    private bool _hostedDelete;
    private bool _hostedLeft;
    private bool _hostedRight;
    private bool _hostedHome;
    private bool _hostedEnd;
    private bool _hostedSelectAll;
    private string _hostedComposition = "";
    /// <summary>The focused field's rectangle in physical pixels, so an input method can point at it.</summary>
    private ToolInputRect _hostedFieldRect;
    /// <summary>Whether composition is currently switched on for us, so it is switched back exactly once.</summary>
    private bool _imeOn;
    private bool _imeFailureLogged;
    private bool _hostedDragging;
    private bool _hostedOverflowLogged;
    private string _hostedFailureLogged = "";
    /// <summary>The ids of the hosted controls last written to the log, so one layout is reported once.</summary>
    private string _lastHostedIds = "";
    private float _hostedPointerX;
    private float _hostedPointerY;
    private bool _hostedPointerValid;
    /// <summary>Rail and entry controls, whose hit rectangles are physical screen pixels.</summary>
    private readonly List<Control> _active = new();
    /// <summary>Panel controls, whose hit rectangles are canvas units before the canvas scale is applied.</summary>
    private readonly List<Control> _panelActive = new();
    private float _lastRailScale = 1f;
    /// <summary>The master icon's identity: it owns the open/close toggle and the phone emblem.</summary>
    private static readonly ToolModule MasterModule = new("rukari.lib.runtime.master", "工具", "master");
    /// <summary>
    /// The module card's own name. It names the column rather than the open module, which is what the panel header
    /// is for, so a reader can tell the two levels apart at a glance.
    /// </summary>
    private const string ModuleCardTitle = "扩展工具";
    private ToolInputRect[] _childRects = Array.Empty<ToolInputRect>();
    private ToolInputRect _panelHit;
    private ToolInputRect _masterRect;
    private ToolInputRect _catchAll;
    private int _lastPixelWidth;
    private int _lastPixelHeight;
    private string _moduleTitle = "Rukari";
    private bool _disposed;
    private bool _focused;
    private int _releaseKeyboardFrames;
    /// <summary>Tree depth drawn this frame: 0 = rail only, 1 = entries, 2 = entries and the panel.</summary>
    private int _depth;
    private float _scale = 1f;
    private float _panelX;
    private float _panelY;
    private string _query = "";
    private int _listPage;
    private readonly ToolListNavigation _listNavigation = new();
    private string? _navigationPageId;
    private ToolListNavigationOptions? _navigationOptions;
    private IReadOnlyList<ToolListItem>? _navigationItems;
    private string[] _navigationLabels = Array.Empty<string>();
    private ToolListNavigationView? _navigationView;
    private string? _pressedKey;
    private bool _activeSearch;

    internal ToolboxDrawer(ToolInputService input, Action<string> log)
    {
        _input = input;
        _log = log;
        var registration = input.RegisterRegion("rukari.lib.runtime", "shared-toolbox");
        if (!registration.Success) throw new InvalidOperationException(registration.Error!.Message);
        _region = registration.Value;
        GameObject? canvasObject = null;
        Font? font = null;
        try
        {
            font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei UI", 22) ?? Font.GetDefault();
            _font = font;
            canvasObject = new GameObject("Rukari shared toolbox");
            _canvasObject = canvasObject;
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            _canvas = canvas;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32710;
            canvas.pixelPerfect = false;
            _scaler = canvasObject.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            Object.DontDestroyOnLoad(canvasObject);

            var panel = CreateImage("ToolboxDrawer", canvasObject.transform, "panel");
            _panelObject = panel.GameObject;
            _panelRect = panel.Rect;
            // Background artwork must be below both snapshot and hosted controls, and must share their parent
            // lifetime. The actual settings sheet already carries its header separator and shadow.
            _panelFrame = new OfficialPopupFrame(panel.Rect, panel.Image);
            _title = CreateText("Title", panel.Rect, "Rukari 工具箱", 30, TextAnchor.MiddleCenter);
            _title.fontStyle = FontStyle.Bold;
            _title.color = OfficialPopupFrame.Heading;
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;
            _summary = CreateText("Summary", panel.Rect, "", 18, TextAnchor.UpperLeft);
            _status = CreateText("Status", panel.Rect, "", 17, TextAnchor.UpperLeft);
            _pageInfo = CreateText("Pagination", panel.Rect, "", 16, TextAnchor.MiddleCenter);
            // The card is created before the rows it holds, because sibling order is draw order: the plate has to
            // sit behind its own rows and the scrollbar has to sit on top of the plate.
            _card = CreateControl("ModuleCard", canvasObject.transform);
            _cardFrame = new OfficialPopupFrame(_card.Rect, _card.Image);
            _cardTitle = CreateText("ModuleCardTitle", canvasObject.transform, "", ToolDrawerLayout.ModuleCardTitleFontSize, TextAnchor.MiddleLeft);
            for (int i = 0; i < _rail.Length; i++) _rail[i] = CreateControl("Rail" + i, canvasObject.transform);
            _scrollTrack = CreateControl("ModuleScrollTrack", canvasObject.transform);
            _scrollThumb = CreateControl("ModuleScrollThumb", canvasObject.transform);
            _cardCollapse = CreateControl("ModuleCollapse", canvasObject.transform);
            // The middle column belongs to the canvas, not to the panel: it pops out of the rail and stays
            // visible while the panel above it is closed, which is what makes the tree collapsible one level
            // at a time.
            for (int i = 0; i < _entries.Length; i++) _entries[i] = CreateControl("Entry" + i, canvasObject.transform);
            _master = CreateControl("ToolboxMaster", canvasObject.transform);
            _close = CreateControl("Close", panel.Rect);
            var closeGlyph = CreateImage("OfficialCloseGlyph", _close.Rect, "solid");
            _closeGlyph = (closeGlyph.Rect, closeGlyph.Image);
            _closeGlyph.Image.enabled = false;
            _search = CreateControl("Search", panel.Rect);
            _searchClear = CreateControl("ClearSearch", panel.Rect);
            _previous = CreateControl("Previous", panel.Rect);
            _next = CreateControl("Next", panel.Rect);
            for (int i = 0; i < _rows.Length; i++) _rows[i] = CreateControl("Row" + i, panel.Rect);
            for (int i = 0; i < _actions.Length; i++) _actions[i] = CreateControl("Action" + i, panel.Rect);
            log("Shared compact toolbox created. Only the active page is polled; feature actions stay in their providers.");
        }
        catch
        {
            if (canvasObject is not null) Object.Destroy(canvasObject);
            // Font.GetDefault may be shared; do not destroy a font borrowed from the engine.
            _region.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Draws the three tree levels from the state the service owns. <paramref name="depth"/> is 0 (rail only),
    /// 1 (the open module's entries beside the rail) or 2 (its panel as well); <paramref name="openEntryIndex"/>
    /// is which of those entries the panel belongs to. Every level is drawn from one placement, so the columns
    /// cannot disagree about where they are.
    ///
    /// <paramref name="hosted"/> is the content of a page that draws itself; a null value means the page is an
    /// ordinary snapshot. Both shapes share this one frame, one header and one close button.
    /// </summary>
    internal void Tick(int depth, IReadOnlyList<ToolModule> modules, int selectedModuleIndex,
        IReadOnlyList<ToolEntry> entries, int openEntryIndex, ToolboxService.Page? selected,
        IToolPanelContent? hosted, ToolPageSnapshot snapshot, Action toggle, Action<string> openModule,
        Action<string> openEntry, Action collapseOne, Action<ToolAction> action)
    {
        if (_disposed) return;
        int pixelWidth = Screen.width;
        int pixelHeight = Screen.height;
        if (pixelWidth <= 0 || pixelHeight <= 0) { Hide(); return; }
        _lastPixelWidth = pixelWidth;
        _lastPixelHeight = pixelHeight;
        int nextDepth = Math.Clamp(depth, 0, 2);
        // Walking back a level invalidates a press that belonged to the level that just closed, including the
        // search box that lived in the panel.
        if (nextDepth < _depth) ResetInteraction();
        _depth = nextDepth;
        bool expanded = _depth >= 2;
        _canvasObject.SetActive(true);
        IReadOnlyList<ToolListItem> original = snapshot.Items ?? Array.Empty<ToolListItem>();
        bool hasList = snapshot.Items is not null;
        _activeSearch = hasList && snapshot.AllowSearch && _input.SupportsKeyboardCapture;
        BindListNavigation(selected?.Id, snapshot.ListNavigation, original);
        IReadOnlyList<ToolListItem> filtered = _navigationOptions?.SearchMode == ToolListSearchMode.Locate
            ? original
            : _navigationView is not null ? _navigationView.ItemIndices.Select(i => original[i]).ToArray()
            : string.IsNullOrWhiteSpace(_query) ? original.ToArray()
                : original.Where(i => (i.Label ?? "").Contains(_query, StringComparison.OrdinalIgnoreCase)).ToArray();
        // -1 means "no module is open" and must stay -1: clamping it to the first module would light a button the
        // user never pressed, and the next press on it would read as a close. The panel title needs a real module
        // instead, so it clamps separately, and it is only drawn while a module IS open.
        int litModule = ToolDrawerLayout.HighlightedModuleIndex(selectedModuleIndex, modules.Count);
        int titledModule = modules.Count == 0 ? 0 : Math.Clamp(selectedModuleIndex, 0, modules.Count - 1);
        // A module with several pages no longer needs a switcher row inside the panel: those pages are the middle
        // column, so the panel is always measured as a single-page sheet. A hosted page states the sheet height
        // it wants instead, and the same panel scale still shrinks it to fit the window.
        ToolDrawerLayout layout = hosted is null
            ? ToolDrawerLayout.Measure(1, filtered.Count, snapshot.Buttons.Count,
                hasList, _activeSearch, snapshot.Summary, snapshot.Status)
            : ToolDrawerLayout.MeasureHosted(hosted.PreferredHeight, RequestedWidthOf(hosted));
        _contentWidth = layout.InnerWidth;
        _sheetWidth = layout.PanelWidth;
        _sheetHeight = layout.Height;
        // The panel's own lifecycle follows the same condition that decides whether it is drawn, so collapsing a
        // level, switching page and leaving the node editor all hide the page exactly once.
        SyncHostedLifecycle(expanded && hosted is not null ? hosted : null);
        // Exactly ONE page owns the panel, and this is the single place that decides it. Each page kind draws into
        // its own control pool, and the branches below return early, so hiding the other pool here is what stops a
        // page from leaving the previous page's rows and buttons on screen — the panel used to show two pages at
        // once the moment a page that draws itself followed a page described by a snapshot.
        if (hosted is not null) HideSnapshotControls();
        else HideHostedControls();
        // The rail is permanent and independent of the panel: opening, closing or switching a module never
        // moves a rail button, and the panel never grows past the reserved left band. The entries are gated by
        // the tree depth, not by the panel, so closing the panel leaves the column the user came from.
        int visibleModuleRows = ToolDrawerLayout.VisibleModuleRows(pixelHeight, modules.Count);
        _moduleFirstRow = ToolScroll.ClampFirst(_moduleFirstRow, modules.Count, visibleModuleRows);
        ToolDrawerPlacement placement = layout.PlaceTree(pixelWidth, pixelHeight, modules, titledModule,
            entries, openEntryIndex, _depth >= 1, _moduleFirstRow);
        _moduleFirstRow = placement.FirstVisibleRow;
        float railScale = ToolDrawerLayout.RailScaleFor(pixelWidth, pixelHeight);
        _lastRailScale = railScale;
        _scale = placement.Scale;
        _scaler.scaleFactor = _scale;
        _canvas.scaleFactor = _scale;
        float height = layout.Height;
        _panelX = placement.Panel.X / _scale;
        _panelY = placement.Panel.Y / _scale;
        _moduleTitle = modules.Count > 0 ? modules[titledModule].Title : "Rukari";
        // The master icon is the only permanently visible control and it NEVER moves: opening only reveals the
        // child stack above it. Every tracked rectangle comes from this one placement, assigned before any
        // branch, because the collapsed path publishes too and a 0x0 rectangle would disarm the input guard.
        _masterRect = placement.Master;
        _catchAll = placement.CatchAll;
        // Rail and entry rectangles are physical pixels, so they are published unchanged and only divided by the
        // canvas scale when drawn; panel rectangles are canvas units and are published through _panelHit alone.
        // The card is published as one rectangle rather than one per row: the guard already covers it, and the
        // list of published rectangles is what has to stay bounded when a mod author installs thirty mods.
        var published = new List<ToolInputRect>(placement.Children.Count + placement.Entries.Count + 2);
        published.AddRange(placement.Children);
        published.AddRange(placement.Entries);
        if (placement.Card.IsValid) published.Add(placement.Card);
        published.Add(_masterRect);
        _childRects = published.ToArray();
        _panelHit = new ToolInputRect(placement.Panel.X, placement.Panel.Y, placement.Panel.Width, placement.Panel.Height);
        _active.Clear();
        _panelActive.Clear();
        for (int slot = 0; slot < _rail.Length; slot++)
        {
            // The module rows exist only while the tree is open; the big icon is the way in and out.
            if (slot >= placement.Children.Count || _depth < 1) { _rail[slot].GameObject.SetActive(false); continue; }
            // A row is a slot, not a module: with the column scrolled, the first row is no longer the first mod,
            // and a button that opened "the module with this index" would open the wrong one.
            int moduleIndex = slot < placement.VisibleModules.Count ? placement.VisibleModules[slot] : slot;
            if (moduleIndex < 0 || moduleIndex >= modules.Count) { _rail[slot].GameObject.SetActive(false); continue; }
            ToolModule module = modules[moduleIndex];
            // Each button must open ITS OWN module. Capturing the currently selected module's first page here made
            // every button open whatever was already displayed, which is why both opened the same panel.
            LayoutModuleButton(_rail[slot], module, placement.Children[slot], moduleIndex == litModule,
                () => openModule(module.Id));
        }
        for (int i = 0; i < _entries.Length; i++)
        {
            if (i >= entries.Count || _depth < 1) { _entries[i].GameObject.SetActive(false); continue; }
            ToolEntry entry = entries[i];
            LayoutEntry(_entries[i], entry, i < placement.Entries.Count ? placement.Entries[i] : default,
                i == openEntryIndex, () => openEntry(entry.Id));
        }
        LayoutModuleCard(placement, collapseOne);
        LayoutMasterButton(_master, _masterRect, toggle);
        // The wheel and the scrollbar thumb are read from the same placement the card was just drawn from, so the
        // thumb the pointer is dragging is the thumb on screen.
        HandleModuleScroll(placement, modules.Count);
        _panelObject.SetActive(expanded);
        if (!expanded)
        {
            _input.SetKeyboardCapture(false);
            _region.Update(BuildInputRects(includePanel: false, pixelWidth, pixelHeight));
            _input.ReportPublishedRegions();
            LogLayoutOnce(pixelWidth, pixelHeight, modules.Count, placement);
            HandleKeyboard(collapseOne);
            HandlePointer();
            return;
        }

        SetRect(_panelRect, _panelX, _panelY, placement.Panel.Width / _scale, height);
        float cursor = height - ToolDrawerLayout.Padding;
        // Header geometry comes from the layout contract, so the close button can never be placed past the
        // panel's own frame again. It used to sit at ContentWidth + 8, four units outside the panel width, and it
        // now measures against THIS page's sheet width rather than the shared default one.
        ToolInputRect titleBounds = ToolDrawerLayout.TitleBounds(layout.PanelWidth, height);
        ToolInputRect closeBounds = ToolDrawerLayout.CloseBounds(layout.PanelWidth, height);
        _title.text = ToolDrawerLayout.FitSingleLine(_moduleTitle, _title.fontSize, titleBounds.Width);
        LayoutText(_title, titleBounds.X, titleBounds.Y, titleBounds.Width, titleBounds.Height);
        float headerHeight = ToolDrawerLayout.Padding + ToolDrawerLayout.HeaderRowHeight - 4f;
        OfficialPopupFrame.CenterTitle(_title, height - headerHeight, headerHeight);
        _panelFrame.Layout(placement.Panel.Width / _scale, height,
            headerHeight, titleBounds, _title.preferredWidth);
        Layout(_close, "close", "×", closeBounds.X, closeBounds.Y, closeBounds.Width, closeBounds.Height, true, false, collapseOne);
        DrawCloseGlyph(closeBounds.Width, closeBounds.Height);
        cursor -= ToolDrawerLayout.HeaderRowHeight;
        if (hosted is not null)
        {
            DrawHosted(hosted, layout.ContentHeight);
            PublishExpandedFrame(pixelWidth, pixelHeight, modules.Count, placement, collapseOne);
            return;
        }
        // The hosted pool was dropped above, so nothing from a page that draws itself can claim this press.

        _summary.gameObject.SetActive(layout.SummaryHeight > 0);
        if (layout.SummaryHeight > 0)
        {
            _summary.text = ToolboxService.Limit(snapshot.Summary, 900);
            LayoutText(_summary, ContentX, cursor - layout.SummaryHeight, ContentWidth, layout.SummaryHeight);
            cursor -= layout.SummaryHeight + 10;
        }
        if (_activeSearch)
        {
            string searchText = _query + (_focused ? SafeComposition() : "");
            Layout(_search, "search", string.IsNullOrEmpty(searchText)
                    ? (_focused ? (_navigationOptions?.SearchMode == ToolListSearchMode.Locate ? "输入名称定位…" : "输入名称筛选…") : "点击搜索名称")
                    : searchText + (_focused ? " |" : ""),
                ContentX, cursor - 38, ContentWidth - 66, 38, true, _focused, () => _focused = true, skin: "solid");
            Layout(_searchClear, "search-clear", "清空", ContentX + ContentWidth - 60, cursor - 38, 60, 38,
                _query.Length != 0, false, () => SetSearchQuery(""));
            _search.Label.alignment = TextAnchor.MiddleLeft;
            cursor -= 46;
        }
        else
        {
            _search.GameObject.SetActive(false); _searchClear.GameObject.SetActive(false);
            _focused = false;
        }

        int rowsPerPage = MaxRows;
        int pageCount = Math.Max(1, (filtered.Count + rowsPerPage - 1) / rowsPerPage);
        _listPage = Math.Clamp(_listPage, 0, pageCount - 1);
        for (int i = 0; i < _rows.Length; i++)
        {
            int index = _listPage * rowsPerPage + i;
            if (!hasList || i >= layout.VisibleRows || index >= filtered.Count) { _rows[i].GameObject.SetActive(false); continue; }
            ToolListItem row = filtered[index];
            bool searchMatch = _navigationOptions?.SearchMode == ToolListSearchMode.Locate
                && _navigationView?.MatchedIndex == index;
            Layout(_rows[i], "item:" + (selected?.Id ?? "") + ":" + row.Id,
                (row.Selected ? "✓ " : searchMatch ? "› " : "") + row.Label, ContentX, cursor - 37 - i * 42, ContentWidth, 37,
                row.Enabled, row.Selected || searchMatch, () => action(new ToolAction(snapshot.ItemActionId, row.Id)));
            _rows[i].Label.alignment = TextAnchor.MiddleLeft;
        }
        cursor -= layout.VisibleRows * 42;
        _pageInfo.gameObject.SetActive(layout.EmptyList || layout.Pagination);
        if (layout.EmptyList)
        {
            _pageInfo.text = string.IsNullOrWhiteSpace(_query) ? "暂无可用项目" : "没有匹配的项目";
            LayoutText(_pageInfo, ContentX, cursor - 32, ContentWidth, 32);
            cursor -= 38;
        }
        if (layout.Pagination)
        {
            Layout(_previous, "previous", "上一页", ContentX, cursor - 34, 105, 34, _listPage > 0, false, () => SetListPage(_listPage - 1));
            Layout(_next, "next", "下一页", ContentX + ContentWidth - 105, cursor - 34, 105, 34, _listPage + 1 < pageCount, false, () => SetListPage(_listPage + 1));
            bool noMatch = _navigationOptions?.SearchMode == ToolListSearchMode.Locate
                && !string.IsNullOrWhiteSpace(_query) && _navigationView?.MatchedIndex is null;
            _pageInfo.text = $"{_listPage + 1}/{pageCount} · {(noMatch ? "未找到" : filtered.Count + " 项")}";
            LayoutText(_pageInfo, ContentX + 112, cursor - 34, ContentWidth - 224, 34);
            cursor -= 44;
        }
        else
        {
            _previous.GameObject.SetActive(false); _next.GameObject.SetActive(false);
        }

        int buttonCount = Math.Min(snapshot.Buttons.Count, _actions.Length);
        for (int i = 0; i < _actions.Length; i++)
        {
            if (i >= buttonCount) { _actions[i].GameObject.SetActive(false); continue; }
            ToolButton button = snapshot.Buttons[i];
            Layout(_actions[i], "action:" + (selected?.Id ?? "") + ":" + button.Id, button.Label,
                ContentX + (i % 2) * (ToolDrawerLayout.ContentWidth / 2 + 5), cursor - 39 - (i / 2) * 45,
                ToolDrawerLayout.ContentWidth / 2, 39, button.Enabled, button.Highlighted,
                () => action(new ToolAction(button.Id, button.Value)), face: button.Skin);
        }
        cursor -= layout.ActionRows * 45;
        _status.gameObject.SetActive(layout.StatusHeight > 0);
        if (layout.StatusHeight > 0)
        {
            cursor -= 10;
            _status.text = ToolboxService.Limit(snapshot.Status, 600);
            LayoutText(_status, ContentX, cursor - layout.StatusHeight, ContentWidth, layout.StatusHeight);
        }

        // Rectangles were assigned once at the top of this method from the same placement; the panel rect is the
        // only one that only matters while the panel is showing.
        PublishExpandedFrame(pixelWidth, pixelHeight, modules.Count, placement, collapseOne);
    }

    /// <summary>
    /// The tail every expanded frame shares: publish the input region, log a changed layout, then route the
    /// keyboard and the pointer. A hosted drag publishes capture-all, which is what keeps a drag that wanders
    /// off the panel from clicking anything in the official editor underneath.
    /// </summary>
    private void PublishExpandedFrame(int pixelWidth, int pixelHeight, int moduleCount,
        ToolDrawerPlacement placement, Action collapseOne)
    {
        _region.Update(BuildInputRects(includePanel: true, pixelWidth, pixelHeight),
            captureAllPointer: _hostedDragging || _moduleScrollDrag);
        _input.ReportPublishedRegions();
        LogLayoutOnce(pixelWidth, pixelHeight, moduleCount, placement);
        HandleKeyboard(collapseOne);
        HandlePointer();
        _input.SetKeyboardCapture(_focused || _releaseKeyboardFrames > 0 || _hostedField is not null);
        if (_releaseKeyboardFrames > 0) _releaseKeyboardFrames--;
    }

    /// <summary>
    /// Escape walks the tree back one level, but only while the pointer rests on our own surfaces. Taking the
    /// key wherever the user is looking would steal the official editor's Escape handling, so this stays a local
    /// gesture on top of the rail instead of a global hotkey.
    /// </summary>
    private void HandleKeyboard(Action collapseOne)
    {
        // A hosted page's focused field takes the keyboard the same way the search box does, and for the same
        // reason: the drawer is the only owner of input, so a page cannot read it and must be handed what arrived.
        HandleHostedField();
        // Escape walks the tree back one level, but only while the pointer rests on our own surfaces and nothing of
        // ours is taking the keyboard. A hosted page's focused text field owns Escape while it is editing: taking
        // it there would close the panel the user is typing into. Taking the key wherever the user is looking would
        // steal the official editor's Escape handling, so this stays a local gesture on top of the rail instead of
        // a global hotkey.
        if (!_focused && _hostedField is null && _depth > 0)
        {
            var pointer = UnityEngine.Input.mousePosition;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) && OverOurOwnSurfaces(pointer.x, pointer.y))
            {
                // Keep the depth this frame consistent with the tree we just asked to collapse; the next Tick
                // re-reads the authoritative state anyway.
                _depth--;
                collapseOne();
                return;
            }
        }
        if (!_focused || !_activeSearch) return;
        foreach (char ch in UnityEngine.Input.inputString ?? "")
        {
            if (ch == '\b')
            {
                if (_query.Length > 0)
                {
                    int remove = _query.Length >= 2 && char.IsLowSurrogate(_query[^1]) && char.IsHighSurrogate(_query[^2]) ? 2 : 1;
                    SetSearchQuery(_query[..^remove]);
                }
            }
            else if (ch is '\r' or '\n')
            {
                if (SafeComposition().Length != 0) continue;
                SubmitListSearch();
                _focused = false;
                _releaseKeyboardFrames = 2;
            }
            else if (ch == '\u001b') { _focused = false; _releaseKeyboardFrames = 2; }
            else if (!char.IsControl(ch) && _query.Length < 120) SetSearchQuery(_query + ch);
        }
        bool control = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
        if (control && UnityEngine.Input.GetKeyDown(KeyCode.V))
        {
            string pasted = new string(Clipboard().Where(ch => !char.IsControl(ch)).Take(120).ToArray());
            if (pasted.Length > 0) SetSearchQuery(pasted);
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { _focused = false; _releaseKeyboardFrames = 2; }
    }

    /// <summary>
    /// Collects this frame's keystrokes for a hosted page's focused field. The buffers are cleared here and read by
    /// the page on its next frame, so a page always sees a whole frame's typing as one batch and a page that stops
    /// focusing a field simply stops receiving anything.
    /// </summary>
    private void HandleHostedField()
    {
        _hostedTyped = "";
        _hostedBackspace = false;
        _hostedSubmit = false;
        _hostedCancel = false;
        _hostedDelete = false;
        _hostedLeft = false;
        _hostedRight = false;
        _hostedHome = false;
        _hostedEnd = false;
        _hostedSelectAll = false;
        _hostedComposition = "";
        if (_hostedField is null)
        {
            // The shared search field also needs composition for Chinese/Japanese resource names.
            SetIme(_focused && _activeSearch);
            return;
        }
        foreach (char ch in UnityEngine.Input.inputString ?? "")
        {
            if (ch == '\b') _hostedBackspace = true;
            else if (ch is '\r' or '\n') _hostedSubmit = true;
            else if (ch == '\u001b') _hostedCancel = true;
            else if (!char.IsControl(ch) && _hostedTyped.Length < 240) _hostedTyped += ch;
        }
        bool control = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
        if (control && UnityEngine.Input.GetKeyDown(KeyCode.V))
        {
            // Pasting is the one path that works whatever the input method is doing: a caption is usually written
            // somewhere else first, and Windows delivers the clipboard rather than a character stream.
            string pasted = Clipboard();
            if (pasted.Length != 0 && _hostedTyped.Length + pasted.Length <= 480) _hostedTyped += pasted;
        }
        if (control && UnityEngine.Input.GetKeyDown(KeyCode.A)) _hostedSelectAll = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Delete)) _hostedDelete = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) _hostedLeft = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) _hostedRight = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Home)) _hostedHome = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.End)) _hostedEnd = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) _hostedCancel = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
            _hostedSubmit = true;
        // An input method only appears when the game asks for composition, and what it is composing has to be shown
        // by us: the engine hands over the run-in-progress but draws nothing itself.
        SetIme(true);
        _hostedComposition = SafeComposition();
    }

    /// <summary>
    /// Turns input-method composition on while one of our fields is focused, and points it at that field so the
    /// candidate window appears next to what is being typed instead of in a corner.
    /// </summary>
    private void SetIme(bool on)
    {
        try
        {
            if (_imeOn == on)
            {
                if (on) AimCompositionCursor();
                return;
            }
            UnityEngine.Input.imeCompositionMode = on
                ? UnityEngine.IMECompositionMode.On
                : UnityEngine.IMECompositionMode.Auto;
            _imeOn = on;
            if (on) AimCompositionCursor();
        }
        catch (Exception ex)
        {
            if (!_imeFailureLogged)
            {
                _imeFailureLogged = true;
                _log?.Invoke($"Input method composition could not be switched on: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void AimCompositionCursor()
    {
        ToolInputRect rect = _focused && _activeSearch
            ? new ToolInputRect(_search.Hit.X * _scale, _search.Hit.Y * _scale,
                _search.Hit.Width * _scale, _search.Hit.Height * _scale)
            : _hostedFieldRect;
        if (!rect.IsValid) return;
        try
        {
            UnityEngine.Input.compositionCursorPos = new Vector2(
                rect.X + 12f, rect.Y + rect.Height / 2f);
        }
        catch (Exception)
        {
            // The cursor position is a hint; a platform that refuses it still composes.
        }
    }

    private string SafeComposition()
    {
        try
        {
            return UnityEngine.Input.compositionString ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>The clipboard as text, with newlines folded so a pasted paragraph stays one caption line.</summary>
    private string Clipboard()
    {
        try
        {
            string raw = UnityEngine.GUIUtility.systemCopyBuffer ?? "";
            return raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Draws a self-hosted page's content and feeds it this frame's pointer. The page never sees a game object:
    /// it asks for controls inside its own rectangle and the drawer reuses its pooled ones, which is what keeps
    /// one theme, one input owner and one canvas across every mod.
    ///
    /// <paramref name="cursor"/> is the bottom of the panel's remaining space, so the content is everything
    /// between the panel's padding and the header.
    /// </summary>
    /// <param name="contentHeight">Usable height of the content rectangle, from the layout contract.</param>
    /// </summary>
    private void DrawHosted(IToolPanelContent hosted, float contentHeight)
    {
        _hostedDragging = false;
        var content = new ToolInputRect(0f, 0f, _contentWidth, Math.Max(1f, contentHeight));
        var raw = UnityEngine.Input.mousePosition;
        // Panel-local canvas units, then content-local: the page only ever sees its own coordinate space.
        float pointerX = raw.x / _scale - (_panelX + ContentX);
        float pointerY = raw.y / _scale - (_panelY + ToolDrawerLayout.Padding);
        float deltaX = _hostedPointerValid ? pointerX - _hostedPointerX : 0f;
        float deltaY = _hostedPointerValid ? pointerY - _hostedPointerY : 0f;
        _hostedPointerX = pointerX;
        _hostedPointerY = pointerY;
        _hostedPointerValid = true;
        bool pressed = UnityEngine.Input.GetMouseButtonDown(0);
        bool released = UnityEngine.Input.GetMouseButtonUp(0);
        // A press is resolved against the previous frame's layout, because it arrives before this frame's page
        // has drawn the control it landed on; a page's layout is stable from one frame to the next.
        if (pressed) _hostedPressed = ToolPanelBuilder.HitTest(_hostedLast, pointerX, pointerY);
        var pointer = new ToolPanelPointer(pointerX, pointerY, UnityEngine.Input.GetMouseButton(0),
            pressed, released, deltaX, deltaY);
        // The same rectangle in physical pixels: a page that keeps its own scaled canvas places it from this
        // instead of repeating the canvas arithmetic, so the two cannot disagree by a few pixels.
        var contentPixels = ToolDrawerLayout.HostedElementPixels(_panelHit, _scale, content);
        // What the keyboard did last frame, so a page with a focused field can apply it to its own value.
        var keyboard = new ToolPanelKeyboard(_hostedTyped, _hostedBackspace, _hostedSubmit, _hostedCancel,
            _hostedDelete, _hostedLeft, _hostedRight, _hostedHome, _hostedEnd, _hostedSelectAll, _hostedComposition);
        var builder = new ToolPanelBuilder(content, pointer, _hostedPressed, contentPixels, keyboard);
        string failure = "";
        try
        {
            hosted.Draw(builder);
            _hostedField = builder.FocusedField;
        }
        catch (Exception ex)
        {
            // A page's bug must not take the shared toolbox down with it: report it on the page itself, once.
            failure = ex.GetType().Name + ": " + ex.Message;
            builder = new ToolPanelBuilder(content, pointer, _hostedPressed, contentPixels, keyboard);
            _hostedField = null;
        }
        if (!string.Equals(failure, _hostedFailureLogged, StringComparison.Ordinal))
        {
            _hostedFailureLogged = failure;
            if (failure.Length != 0) _log?.Invoke($"Hosted tool page failed to draw and was replaced by its error message: {failure}");
        }
        IReadOnlyList<ToolPanelElement> elements = builder.Elements;
        _hostedLast = elements as ToolPanelElement[] ?? elements.ToArray();
        DrawHostedElements(elements, failure);
        _hostedDragging = builder.IsDragging;
        if (released) _hostedPressed = null;
    }

    private void DrawHostedElements(IReadOnlyList<ToolPanelElement> elements, string failure)
    {
        EnsureHostedPool(elements.Count);
        int used = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            if (used >= _hosted.Length)
            {
                if (!_hostedOverflowLogged)
                {
                    _hostedOverflowLogged = true;
                    _log?.Invoke($"A hosted tool page asked for more than {_hosted.Length} controls; the rest were not drawn.");
                }
                break;
            }
            ToolPanelElement element = elements[i];
            // An area draws nothing by itself: it is a rectangle the page hit-tests, and a page that wants a
            // visible frame asks for a plate as well.
            if (element.Kind == ToolPanelElementKind.Area) continue;
            used = DrawHostedElement(_hosted[used], element, used);
        }
        if (failure.Length != 0)
        {
            if (used < _hosted.Length)
            {
                var bounds = new ToolInputRect(0f, 0f, _contentWidth, ToolDrawerLayout.LineHeight(16) * 3);
                used = DrawHostedElement(_hosted[used],
                    new ToolPanelElement("hosted-error", ToolPanelElementKind.Status, bounds, failure,
                        ToolSurfaceStyle.Normal, 16, true, false), used);
            }
        }
        for (int i = used; i < _hostedDrawn; i++) _hosted[i].GameObject.SetActive(false);
        _hostedDrawn = used;
        LogHostedOnce(elements, used);
    }

    /// <summary>
    /// Writes down every control the renderer actually put on screen, whenever the set of them changes. A page logs
    /// what it asked for; this logs what was drawn. A control visible on screen that appears in neither list came
    /// from somewhere else entirely, which is the only way to settle a report of "there is an extra square here".
    /// </summary>
    private void LogHostedOnce(IReadOnlyList<ToolPanelElement> elements, int used)
    {
        var signature = new System.Text.StringBuilder();
        foreach (ToolPanelElement element in elements) signature.Append(element.Id).Append('|');
        string ids = signature.ToString();
        if (string.Equals(ids, _lastHostedIds, StringComparison.Ordinal)) return;
        _lastHostedIds = ids;
        var text = new System.Text.StringBuilder($"[hosted] 绘制 {used} 个控件：");
        for (int i = 0; i < used && i < _hosted.Length; i++)
        {
            RectTransform rect = _hosted[i].Rect;
            text.Append(' ').Append(_hosted[i].Key).Append('@')
                .Append(rect.anchoredPosition.x.ToString("F0")).Append(',')
                .Append(rect.anchoredPosition.y.ToString("F0")).Append(' ')
                .Append(rect.sizeDelta.x.ToString("F0")).Append('x').Append(rect.sizeDelta.y.ToString("F0"))
                .Append(';');
        }
        _log?.Invoke(text.ToString());
    }

    private int DrawHostedElement(Control control, ToolPanelElement element, int index)
    {
        control.Key = element.Id;
        // Panel-local, because every hosted control is a child of the panel object. The sheet's own screen position
        // is applied by that parent transform, and adding it here as well drew the whole page one sheet-width to
        // the right and one sheet-height up, outside the frame it was supposed to be inside. The pointer side of
        // the same conversion (see DrawHosted) always subtracted only the panel origin, which is why a page's
        // controls were clickable somewhere other than where they were drawn.
        ToolInputRect drawn = ToolDrawerLayout.HostedElementBounds(element.Bounds);
        float x = drawn.X;
        float y = drawn.Y;
        float w = Math.Max(1f, drawn.Width);
        float h = Math.Max(1f, drawn.Height);
        control.GameObject.SetActive(true);
        switch (element.Kind)
        {
            case ToolPanelElementKind.Separator:
                // A hairline at the top of the band the builder reserved for it.
                SetRect(control.Rect, x, y + h - 2f, w, 2f);
                SetRect(control.Label.GetComponent<RectTransform>(), 0, 0, 1, 1);
                control.Label.text = "";
                control.Image.enabled = true;
                if (!ToolTheme.Apply(control.Image, "header")) control.Image.color = ToolTheme.Native(ToolPalette.Header.Background);
                break;
            case ToolPanelElementKind.Text:
            case ToolPanelElementKind.Status:
                SetRect(control.Rect, x, y, w, h);
                SetRect(control.Label.GetComponent<RectTransform>(), 2, 0, Math.Max(1f, w - 4), h);
                control.Label.text = ToolboxService.Limit(element.Text, 4000);
                control.Label.alignment = TextAnchor.UpperLeft;
                control.Label.fontSize = Math.Max(1, element.Size);
                control.Label.color = ToolTheme.Foreground(element.Kind == ToolPanelElementKind.Status ? "button.disabled" : "button");
                control.Image.enabled = false;
                break;
            case ToolPanelElementKind.Field:
                // A text field: left-aligned, and the page composes the whole string it wants drawn — including the
                // caret — so the caret sits where the user's typing will really land. The focused field also tells
                // an input method where to put its candidate window.
                SetRect(control.Rect, x, y, w, h);
                SetRect(control.Label.GetComponent<RectTransform>(), 10, 0, Math.Max(1f, w - 20), h);
                control.Label.text = element.Text;
                control.Label.alignment = TextAnchor.MiddleLeft;
                control.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
                control.Label.fontSize = Math.Max(1, element.Size);
                control.Label.color = ToolTheme.Foreground("button");
                control.Image.enabled = true;
                if (element.Highlighted) _hostedFieldRect = PixelsOf(drawn);
                if (!ToolTheme.Apply(control.Image, element.Highlighted ? "button.selected" : "solid"))
                    control.Image.color = ToolTheme.Native(
                        ToolPalette.ForKey(element.Highlighted ? "button.selected" : "solid").Background);
                break;
            case ToolPanelElementKind.Swatch:
                // A flat rectangle in the page's own colour: no nine-slice, no theme, no label. It is how a page
                // shows the colour it is about to apply, which the shared theme cannot express.
                SetRect(control.Rect, x, y, w, h);
                SetRect(control.Label.GetComponent<RectTransform>(), 0, 0, 1, 1);
                control.Label.text = "";
                control.Image.enabled = true;
                control.Image.sprite = null;
                control.Image.type = Image.Type.Simple;
                control.Image.color = ToolTheme.ParseColour(element.Colour, Color.white);
                break;
            default:
                bool enabled = element.Enabled;
                string skin = !enabled ? "button.disabled"
                    : element.Highlighted ? "button.selected"
                    : ToolSurfaceStyles.ThemeKey(element.Style);
                SetRect(control.Rect, x, y, w, h);
                SetRect(control.Label.GetComponent<RectTransform>(), 6, 0, Math.Max(1f, w - 12), h);
                control.Label.text = element.Text.Length == 0 ? ""
                    : ToolDrawerLayout.FitSingleLine(ToolboxService.Limit(element.Text, 200), element.Size, Math.Max(1f, w - 16));
                control.Label.alignment = TextAnchor.MiddleCenter;
                control.Label.fontSize = Math.Max(1, element.Size);
                control.Label.color = ToolTheme.Foreground(skin);
                control.Image.enabled = true;
                if (element.Kind == ToolPanelElementKind.Button)
                {
                    ToolTheme.CenterButtonLabel(control.Label, w, h, 6f);
                    bool hover = element.Bounds.Contains(_hostedPointerX, _hostedPointerY);
                    bool pressed = hover && _hostedPressed == element.Id && UnityEngine.Input.GetMouseButton(0);
                    bool primary = element.Style == ToolSurfaceStyle.Primary;
                    bool decorated = primary || element.Style == ToolSurfaceStyle.Undo;
                    bool selected = element.Highlighted || element.Style == ToolSurfaceStyle.Selected;
                    control.Label.color = OfficialButtonSkin.Apply(control.Image, w, h, enabled,
                        selected, hover, pressed, decorated, primary, navigation: false);
                }
                else if (!ToolTheme.Apply(control.Image, skin))
                    control.Image.color = ToolTheme.Native(ToolPalette.ForKey(skin).Background);
                break;
        }
        return index + 1;
    }

    /// <summary>Grows the hosted control pool. Doubling keeps the allocation cost off the steady-state frame.</summary>
    private void EnsureHostedPool(int required)
    {
        if (required <= _hosted.Length) return;
        int size = Math.Max(16, _hosted.Length);
        while (size < required) size = Math.Min(size * 2, Math.Max(required, size + 16));
        var grown = new Control[size];
        Array.Copy(_hosted, grown, _hosted.Length);
        for (int i = _hosted.Length; i < size; i++)
        {
            grown[i] = CreateControl("Hosted" + i, _panelObject.transform);
            // A new control must start hidden. A RectTransform that has never been laid out keeps Unity's default
            // 100x100 size and its centre anchor, so a control that is simply never drawn sits as a stray square in
            // the middle of the panel, over whatever the page drew there — and it never moves, because nothing ever
            // places it. Growing the pool creates dozens at once, which is why only one square is ever seen.
            grown[i].GameObject.SetActive(false);
        }
        _hosted = grown;
    }

    private void HideHostedControls()
    {
        _hostedPressed = null;
        _hostedDragging = false;
        _hostedPointerValid = false;
        _hostedLast = Array.Empty<ToolPanelElement>();
        // The page is not on screen any more, so nothing of it may keep the keyboard or the input method.
        _hostedField = null;
        _hostedFieldRect = default;
        _hostedTyped = "";
        _hostedBackspace = _hostedSubmit = _hostedCancel = _hostedDelete = false;
        _hostedLeft = _hostedRight = _hostedHome = _hostedEnd = _hostedSelectAll = false;
        _hostedComposition = "";
        SetIme(false);
        for (int i = 0; i < _hostedDrawn; i++) _hosted[i].GameObject.SetActive(false);
        _hostedDrawn = 0;
    }

    /// <summary>
    /// Hides every control a snapshot page owns: the summary, the search box, the pagination, the item rows, the
    /// action buttons and the status line. The panel's title and close button stay, because they belong to the
    /// frame rather than to a page.
    ///
    /// This exists because a page that draws itself returns before any of those sections are laid out: without it
    /// the previous page's rows keep their last positions and stay on screen under the new page, which reads as
    /// two pages drawn at once. The focused flag goes with them — the search box is not on screen any more, and
    /// leaving it focused would keep the keyboard captured for a field nobody can see.
    /// </summary>
    private void HideSnapshotControls()
    {
        _focused = false;
        _summary.gameObject.SetActive(false);
        _search.GameObject.SetActive(false);
        _searchClear.GameObject.SetActive(false);
        _previous.GameObject.SetActive(false);
        _next.GameObject.SetActive(false);
        _pageInfo.gameObject.SetActive(false);
        _status.gameObject.SetActive(false);
        for (int i = 0; i < _rows.Length; i++) _rows[i].GameObject.SetActive(false);
        for (int i = 0; i < _actions.Length; i++) _actions[i].GameObject.SetActive(false);
    }

    /// <summary>
    /// Whether a physical screen point is inside something this drawer published. Used to keep Escape a local
    /// gesture on our own surfaces instead of a global key that would also fire from the official editor.
    /// </summary>
    private bool OverOurOwnSurfaces(float x, float y)
    {
        foreach (ToolInputRect rect in _childRects)
        {
            if (rect.Contains(x, y)) return true;
        }
        return _depth >= 2 && _panelHit.Contains(x, y);
    }

    /// <summary>
    /// One line per distinct layout, so a screenshot can be matched to the exact rectangles the input guard
    /// published. Without this, a click that lands between the drawn pixel and the hit box is invisible.
    /// </summary>
    private void LogLayoutOnce(int pixelWidth, int pixelHeight, int moduleCount, ToolDrawerPlacement placement)
    {
        var text = new System.Text.StringBuilder();
        text.Append("Shared tool rail layout: screen=").Append(pixelWidth).Append('x').Append(pixelHeight)
            .Append("; modules=").Append(moduleCount)
            .Append("; depth=").Append(_depth)
            .Append("; scale=").Append(_scale.ToString("F3"))
            .Append("; sheet=").Append(_sheetWidth.ToString("F0")).Append('x').Append(_sheetHeight.ToString("F0"))
            .Append("; railScale=").Append(_lastRailScale.ToString("F3"));
        for (int i = 0; i < placement.Children.Count; i++)
        {
            ToolInputRect b = placement.Children[i];
            text.Append("; button").Append(i).Append('=')
                .Append(b.X.ToString("F0")).Append(',').Append(b.Y.ToString("F0")).Append(' ')
                .Append(b.Width.ToString("F0")).Append('x').Append(b.Height.ToString("F0"));
        }
        for (int i = 0; i < placement.Entries.Count; i++)
        {
            ToolInputRect e = placement.Entries[i];
            text.Append("; entry").Append(i).Append('=')
                .Append(e.X.ToString("F0")).Append(',').Append(e.Y.ToString("F0")).Append(' ')
                .Append(e.Width.ToString("F0")).Append('x').Append(e.Height.ToString("F0"));
        }
        if (placement.Card.IsValid)
        {
            ToolInputRect c = placement.Card;
            text.Append("; card=").Append(c.X.ToString("F0")).Append(',').Append(c.Y.ToString("F0")).Append(' ')
                .Append(c.Width.ToString("F0")).Append('x').Append(c.Height.ToString("F0"))
                .Append("; row0=").Append(placement.FirstVisibleRow)
                .Append("; shown=").Append(placement.Children.Count);
            if (placement.ScrollThumb.IsValid)
            {
                ToolInputRect t = placement.ScrollThumb;
                text.Append("; thumb=").Append(t.Y.ToString("F0")).Append(' ').Append(t.Height.ToString("F0"));
            }
        }
        ToolInputRect h = _masterRect;
        text.Append("; handle=").Append(h.X.ToString("F0")).Append(',').Append(h.Y.ToString("F0")).Append(' ')
            .Append(h.Width.ToString("F0")).Append('x').Append(h.Height.ToString("F0"));
        text.Append("; guard=").Append(_catchAll.X.ToString("F0")).Append(',').Append(_catchAll.Y.ToString("F0"))
            .Append(' ').Append(_catchAll.Width.ToString("F0")).Append('x').Append(_catchAll.Height.ToString("F0"));
        if (_depth >= 2)
        {
            text.Append("; panel=").Append(_panelHit.X.ToString("F0")).Append(',').Append(_panelHit.Y.ToString("F0"))
                .Append(' ').Append(_panelHit.Width.ToString("F0")).Append('x').Append(_panelHit.Height.ToString("F0"));
        }
        string line = text.ToString();
        if (string.Equals(line, _lastLayoutLog, StringComparison.Ordinal)) return;
        _lastLayoutLog = line;
        _log?.Invoke(line);
    }

    private void HandlePointer()
    {
        var raw = UnityEngine.Input.mousePosition;
        // The permanent rail and the toggle handle can overlap at screen centre, so they are resolved by
        // priority instead of by list order: a module or entry button always wins, then the handle, then the
        // panel. Rail and entry rectangles are physical pixels while panel rectangles are canvas units, which
        // is why the two lists are kept apart rather than searched as one.
        Control? rail = _active.Count == 0 ? null : _active.LastOrDefault(c => c.Enabled && c.Hit.Contains(raw.x, raw.y));
        Control? master = _master.Enabled && _master.Hit.Contains(raw.x, raw.y) ? _master : null;
        Control? panel = _depth >= 2 && _panelActive.Count != 0
            ? _panelActive.LastOrDefault(c => c.Enabled && c.Hit.Contains(raw.x / _scale, raw.y / _scale))
            : null;
        Control? hovered = rail ?? master ?? panel;
        if (UnityEngine.Input.GetMouseButtonDown(0))
        {
            _pressedKey = hovered?.Key;
            if (hovered?.Key != "search") _focused = false;
        }
        if (UnityEngine.Input.GetMouseButtonUp(0))
        {
            string? key = _pressedKey; _pressedKey = null;
            Control? control = hovered is not null && string.Equals(hovered.Key, key, StringComparison.Ordinal)
                ? hovered
                : null;
            _log?.Invoke($"Shared tool click: at={raw.x:F0},{raw.y:F0}; pressed={key ?? "none"}; hit={hovered?.Key ?? "none"}; fire={control?.Key ?? "none"}; railRects={_childRects.Length}");
            control?.Action?.Invoke();
        }
    }

    internal void ResetInteraction()
    {
        _focused = false;
        _pressedKey = null;
        // A press that belonged to a hosted page before the tree changed level must not survive to become a
        // click when the page is opened again.
        _hostedPressed = null;
        // The scroll position itself survives: collapsing the column and opening it again should show the same
        // rows. Only the drag does not, because its press no longer exists.
        _moduleScrollDrag = false;
        _releaseKeyboardFrames = 0;
        _input.SetKeyboardCapture(false);
        SetIme(false);
    }

    internal void ResetPage()
    {
        // Release the visible page, not the session's saved navigation. Opted-in pages restore their
        // own project/list context on the next Tick; legacy pages still open at their original first page.
        _navigationPageId = null;
        _navigationOptions = null;
        _navigationItems = null;
        _navigationLabels = Array.Empty<string>();
        _navigationView = null;
        _query = "";
        _listPage = 0;
        ResetInteraction();
    }

    private void BindListNavigation(string? pageId, ToolListNavigationOptions? options, IReadOnlyList<ToolListItem> items)
    {
        if (pageId is null || options is null)
        {
            _navigationPageId = null;
            _navigationOptions = null;
            _navigationView = null;
            return;
        }
        if (_navigationPageId != pageId || _navigationOptions != options) ResetInteraction();
        _navigationPageId = pageId;
        _navigationOptions = options;
        if (!ReferenceEquals(_navigationItems, items))
        {
            _navigationItems = items;
            _navigationLabels = items.Select(item => item.Label ?? "").ToArray();
        }
        UpdateListNavigation(_listNavigation.Read(pageId, options.ContextId, options.SearchMode, _navigationLabels, MaxRows));
    }

    private void UpdateListNavigation(ToolListNavigationView view)
    {
        _navigationView = view;
        _query = view.Query;
        _listPage = view.Page;
    }

    private void SetSearchQuery(string query)
    {
        if (_navigationPageId is not null && _navigationOptions is { } options)
            UpdateListNavigation(_listNavigation.SetQuery(_navigationPageId, options.ContextId, options.SearchMode,
                _navigationLabels, MaxRows, query));
        else { _query = query; _listPage = 0; }
    }

    private void SetListPage(int page)
    {
        if (_navigationPageId is not null && _navigationOptions is { } options)
            UpdateListNavigation(_listNavigation.SetPage(_navigationPageId, options.ContextId, options.SearchMode,
                _navigationLabels, MaxRows, page));
        else _listPage = page;
    }

    private void SubmitListSearch()
    {
        if (_navigationPageId is not null && _navigationOptions is { } options)
            UpdateListNavigation(_listNavigation.Locate(_navigationPageId, options.ContextId, options.SearchMode,
                _navigationLabels, MaxRows));
    }

    /// <summary>
    /// Hides the panel and republishes a fail-closed snapshot for this frame. Called whenever the tree changes
    /// level, so the click that opened or closed a level is consumed by the shared guard instead of reaching the
    /// official editor, which reads a click on empty space as "close the node editor". The next Tick replaces
    /// this snapshot with the geometry of whatever levels remain.
    /// </summary>
    internal void BeginLevelChange()
    {
        if (_disposed) return;
        ResetInteraction();
        HideHostedControls();
        _active.Clear();
        _panelActive.Clear();
        // Keep the rail strip published at all times. Clearing the region here would leave one frame in which
        // the still-drawn panel swallows nothing and the click reaches the official editor, which closes the
        // node editor. Only the per-button rectangles are dropped; the catch-all strip stays.
        _panelObject.SetActive(false);
        if (_canvasObject.activeInHierarchy && Rukari.Lib.Runtime.Editor.EditorWorkspaceContext.IsToolWorkspaceVisible)
        {
            foreach (Control railControl in _rail) if (railControl is not null) _active.Add(railControl);
            _active.Add(_master);
            // Consume this closing click before the original NGUI input pass. The next Tick (or
            // Hide) replaces this snapshot without captureAllPointer; no old panel bounds survive.
            bool closingClick = UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButtonUp(0);
            _region.Update(BuildInputRects(includePanel: false, _lastPixelWidth, _lastPixelHeight), captureAllPointer: closingClick);
        }
        else
        {
            _region.Update(Array.Empty<ToolInputRect>());
        }
    }

    /// <summary>
    /// Tells the page leaving the screen once, then the one arriving, whenever the page that owns the panel
    /// changes. Every path that closes the panel calls this with null, so a page can never keep native state alive
    /// after its panel is gone.
    /// </summary>
    private void SyncHostedLifecycle(IToolPanelContent? next)
    {
        if (_hostedSession.Advance(next) is not { } change) return;
        NotifyHosted(change.Hidden, shown: false);
        NotifyHosted(change.Shown, shown: true);
    }

    /// <summary>
    /// Runs one lifecycle callback. A page's bug here must not take the shared toolbox down, for the same reason a
    /// page's draw failure must not: the failure is logged and the next frame carries on.
    /// </summary>
    private void NotifyHosted(IToolPanelContent? page, bool shown)
    {
        if (page is not IToolPanelLifecycle lifecycle) return;
        try
        {
            if (shown) lifecycle.OnShown(); else lifecycle.OnHidden();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Hosted tool page {(shown ? "shown" : "hidden")} callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The sheet width a page asks for, or zero when it implements no sizing and has no opinion.</summary>
    private static float RequestedWidthOf(IToolPanelContent? hosted) =>
        hosted is IToolPanelSizing sizing ? sizing.PreferredWidth : 0f;

    internal void Hide()
    {
        if (_disposed) return;
        ResetInteraction();
        HideHostedControls();
        SyncHostedLifecycle(null);
        _depth = 0;
        _region.Update(Array.Empty<ToolInputRect>());
        _canvasObject.SetActive(false);
    }

    /// <summary>
    /// A panel-local rectangle in canvas units, as physical screen pixels, through the shared placement rule. The
    /// sheet may have been shrunk to fit the window, so a page that keeps its own canvas is told what it really got
    /// instead of deriving it from a scale it cannot see.
    /// </summary>
    private ToolInputRect PixelsOf(ToolInputRect panelLocal) =>
        ToolDrawerLayout.ToScreenPixels(_panelHit, _scale, panelLocal);

    /// <summary>
    /// The input region snapshot, always in physical pixels: the open panel (optional) plus the permanent rail
    /// buttons and the toggle handle, which are already physical. The whole rail strip is appended as a
    /// fail-closed catch-all: if the per-button rectangles are ever wrong, the click is still swallowed instead
    /// of reaching the official editor, which would treat it as "empty space" and close the node editor.
    /// </summary>
    private ToolInputRect[] BuildInputRects(bool includePanel, int pixelWidth, int pixelHeight)
    {
        var strip = _catchAll;
        // A zero-sized catch-all is filtered out by the input service, which disarms the guard and lets every
        // click reach the official editor. That is exactly what a stale assignment order produced once, so make
        // it impossible to publish silently: fall back to a rectangle derived from this frame's geometry.
        if (!strip.IsValid)
        {
            // Cover the whole reserved column: with no usable geometry, the safest fail-closed rectangle is
            // the entire rail band. This path only runs when the placement was unusable and says so.
            strip = ToolDrawerLayout.CatchAllFor(
                pixelWidth, pixelHeight, 0f, pixelHeight, ToolDrawerLayout.RailButtonWidth);
            _log?.Invoke("[region-debug] left column catch-all was invalid; published a derived fallback instead of disarming the guard.");
        }
        var rects = new ToolInputRect[_childRects.Length + (includePanel ? 1 : 0) + 1];
        int index = 0;
        if (includePanel) rects[index++] = _panelHit;
        Array.Copy(_childRects, 0, rects, index, _childRects.Length);
        index += _childRects.Length;
        rects[index] = strip;
        return rects;
    }

    /// <summary>
    /// Draws the module card: the plate every module row lives in, its title band with the card's own name, the
    /// control that collapses the column, and the scrollbar when the list does not fit. The card is drawn in the
    /// rail's physical units like the rows it holds, so the panel's scale never changes its size.
    ///
    /// The rows themselves are laid out by the caller from the same placement, so a row can never be drawn outside
    /// the card that is supposed to contain it.
    /// </summary>
    private void LayoutModuleCard(ToolDrawerPlacement placement, Action collapseOne)
    {
        if (_depth < 1 || !placement.Card.IsValid)
        {
            HideModuleCard();
            return;
        }
        LayoutPlate(_card, placement.Card, "panel.card");
        // The title is the card's own name, not the open module's: those are two different things and the panel
        // header already carries the module. It keeps clear of the collapse control.
        ToolInputRect header = placement.CardHeader;
        if (header.IsValid)
        {
            _cardTitle.gameObject.SetActive(true);
            _cardTitle.text = ModuleCardTitle;
            _cardTitle.fontSize = Math.Max(1, (int)MathF.Round(ToolDrawerLayout.ModuleCardTitleFontSize / _scale));
            _cardTitle.alignment = TextAnchor.MiddleCenter;
            _cardTitle.fontStyle = FontStyle.Bold;
            _cardTitle.color = OfficialPopupFrame.Heading;
            float titleInset = ToolDrawerLayout.ModuleCardCollapseSize + 8f;
            float titleWidth = Math.Max(1f, placement.Card.Width - titleInset * 2f);
            _cardTitle.text = ToolDrawerLayout.FitSingleLine(ModuleCardTitle, _cardTitle.fontSize, titleWidth / _scale);
            LayoutText(_cardTitle, (placement.Card.X + titleInset) / _scale, header.Y / _scale,
                titleWidth / _scale, header.Height / _scale);
            OfficialPopupFrame.CenterTitle(_cardTitle, header.Y / _scale, header.Height / _scale);
            var localTitle = new ToolInputRect(titleInset / _scale, (header.Y - placement.Card.Y) / _scale,
                titleWidth / _scale, header.Height / _scale);
            _cardFrame.Layout(placement.Card.Width / _scale, placement.Card.Height / _scale,
                ToolDrawerLayout.ModuleCardHeaderHeight / _scale, localTitle, _cardTitle.preferredWidth);
        }
        else _cardTitle.gameObject.SetActive(false);
        if (placement.CardCollapse.IsValid)
        {
            ToolInputRect collapse = placement.CardCollapse;
            Layout(_cardCollapse, "module-collapse", "«", collapse.X, collapse.Y, collapse.Width, collapse.Height,
                true, false, collapseOne, panelRelative: false);
            _cardCollapse.Image.enabled = false;
            _cardCollapse.Label.color = OfficialPopupFrame.Heading;
        }
        else _cardCollapse.GameObject.SetActive(false);
        if (placement.ScrollTrack.IsValid) LayoutPlate(_scrollTrack, placement.ScrollTrack, "scroll.track");
        else _scrollTrack.GameObject.SetActive(false);
        if (placement.ScrollThumb.IsValid) LayoutPlate(_scrollThumb, placement.ScrollThumb, "scroll.thumb");
        else _scrollThumb.GameObject.SetActive(false);
    }

    private void HideModuleCard()
    {
        _card.GameObject.SetActive(false);
        _cardTitle.gameObject.SetActive(false);
        _cardCollapse.GameObject.SetActive(false);
        _scrollTrack.GameObject.SetActive(false);
        _scrollThumb.GameObject.SetActive(false);
        _moduleScrollDrag = false;
    }

    private void DrawCloseGlyph(float width, float height)
    {
        _close.Image.enabled = false;
        var sprite = AtlasEmblemSource.GetSprite("Common:Common_Icon_Close", sliced: false);
        _closeGlyph.Image.enabled = sprite is not null;
        _close.Label.text = sprite is null ? "×" : "";
        _close.Label.fontSize = 34;
        _close.Label.color = OfficialPopupFrame.Heading;
        if (sprite is null) return;
        _closeGlyph.Image.sprite = sprite;
        _closeGlyph.Image.type = Image.Type.Simple;
        _closeGlyph.Image.preserveAspect = true;
        _closeGlyph.Image.color = OfficialPopupFrame.Heading;
        SetRect(_closeGlyph.Rect, width * .125f, height * .125f, width * .75f, height * .75f);
    }

    private void LayoutPlate(Control control, ToolInputRect bounds, string skin)
    {
        if (!bounds.IsValid) { control.GameObject.SetActive(false); return; }
        control.GameObject.SetActive(true);
        // Physical pixels divided by the canvas scale: the rail keeps its own size while the panel scales.
        SetRect(control.Rect, bounds.X / _scale, bounds.Y / _scale, bounds.Width / _scale, bounds.Height / _scale);
        SetRect(control.Label.GetComponent<RectTransform>(), 0, 0, 1, 1);
        control.Label.text = "";
        control.Image.enabled = true;
        if (!ToolTheme.Apply(control.Image, skin)) control.Image.color = ToolTheme.Native(ToolPalette.ForKey(skin).Background);
    }

    /// <summary>
    /// Scrolls the module card. A wheel notch moves exactly one row and the thumb can be dragged; both ask
    /// <see cref="ToolScroll"/> the same questions, so the two directions of one gesture cannot disagree about
    /// which row is showing. The thumb is deliberately not one of the pointer's controls: this runs from raw mouse
    /// state so a drag that starts on the thumb keeps working after the pointer leaves it.
    ///
    /// While a drag is in progress the whole pointer is captured, exactly like a hosted page's drag, so releasing
    /// outside the card cannot reach the official editor as a click on empty space.
    /// </summary>
    private void HandleModuleScroll(ToolDrawerPlacement placement, int moduleCount)
    {
        if (_depth < 1 || moduleCount <= 0 || !placement.Card.IsValid)
        {
            _moduleScrollDrag = false;
            return;
        }
        int visible = placement.Children.Count;
        if (!ToolScroll.Needed(moduleCount, visible))
        {
            // Everything fits: the column is not scrollable, so a leftover offset or drag must not survive and
            // silently move the rows the next time a window resize makes them fit again.
            _moduleScrollDrag = false;
            _moduleFirstRow = 0;
            return;
        }
        var raw = UnityEngine.Input.mousePosition;
        bool overCard = placement.Card.Contains(raw.x, raw.y);
        bool pressed = UnityEngine.Input.GetMouseButtonDown(0);
        bool held = UnityEngine.Input.GetMouseButton(0);
        if (pressed && overCard && placement.ScrollThumb.IsValid && placement.ScrollThumb.Contains(raw.x, raw.y))
        {
            _moduleScrollDrag = true;
            _moduleScrollGrab = raw.y - placement.ScrollThumb.Y;
        }
        else if (pressed && overCard && placement.ScrollTrack.IsValid && placement.ScrollTrack.Contains(raw.x, raw.y))
        {
            // A press on the track jumps towards the press and then drags from there, which is what makes the
            // scrollbar usable with one gesture instead of two.
            _moduleFirstRow = ToolScroll.RowForTrackPoint(placement.ScrollTrack, placement.ScrollThumb.Height,
                raw.y, moduleCount, visible);
            _moduleScrollDrag = true;
            _moduleScrollGrab = placement.ScrollThumb.Height / 2f;
        }
        if (_moduleScrollDrag && held)
        {
            _moduleFirstRow = ToolScroll.RowForThumbTop(placement.ScrollTrack, placement.ScrollThumb.Height,
                raw.y - _moduleScrollGrab, moduleCount, visible);
        }
        if (!held) _moduleScrollDrag = false;
        if (overCard && !_focused && !_moduleScrollDrag)
        {
            int rows = ToolScroll.WheelRows(UnityEngine.Input.mouseScrollDelta.y);
            if (rows != 0) _moduleFirstRow = ToolScroll.ClampFirst(_moduleFirstRow + rows, moduleCount, visible);
        }
    }

    /// <summary>
    /// Lays out the one big icon: the permanent way in and out, drawn as the game's own gacha emblem. It carries
    /// no label, because the emblem is the label.
    /// </summary>
    private void LayoutMasterButton(Control control, ToolInputRect bounds, Action action)
    {
        // Physical pixels divided by the canvas scale: the icon keeps its size while the panel scales.
        control.GameObject.SetActive(true);
        SetRect(control.Rect, bounds.X / _scale, bounds.Y / _scale, bounds.Width / _scale, bounds.Height / _scale);
        SetRect(control.Label.GetComponent<RectTransform>(), 0, 0, 1, 1);
        control.Label.text = "";
        control.Key = "master:" + MasterModule.Id;
        control.Action = action;
        control.Enabled = true;
        control.Hit = bounds;
        Sprite? icon = RailIconPainter.Get(MasterModule.Icon, MasterModule.Id);
        if (icon is not null)
        {
            control.Image.enabled = true;
            control.Image.sprite = icon;
            control.Image.type = Image.Type.Simple;
            // The atlas emblems are not square (Arona is 127x163), so keep their aspect instead of stretching
            // them into the button box.
            control.Image.preserveAspect = true;
            control.Image.color = Color.white;
            // Bounded, not per frame: this line is how a wrong atlas lookup was caught, so keep a few samples at
            // startup and then stay silent.
            if (_iconLogCount < 6)
            {
                _iconLogCount++;
                _log?.Invoke($"[icon {_iconLogCount}] MASTER module='{MasterModule.Id}' iconId='{MasterModule.Icon}' sprite='{icon.name}' rect={icon.rect.width}x{icon.rect.height}");
            }
        }
        else if (!ToolTheme.Apply(control.Image, "button.primary"))
        {
            control.Image.color = ToolTheme.Native(ToolPalette.Primary.Background);
        }
        _active.Add(control);
    }

    /// <summary>
    /// Lays out one module button: the small official plate that pops out beside the big icon, filled with the
    /// module's own title. It is drawn in the rail's physical units like the big icon, so opening the panel never
    /// moves or rescales it.
    /// </summary>
    private void LayoutModuleButton(Control control, ToolModule module, ToolInputRect bounds, bool selected, Action action)
    {
        if (!bounds.IsValid) { control.GameObject.SetActive(false); return; }
        control.GameObject.SetActive(true);
        float w = bounds.Width / _scale;
        float h = bounds.Height / _scale;
        float inset = ToolDrawerLayout.ModuleButtonLabelInset / _scale;
        SetRect(control.Rect, bounds.X / _scale, bounds.Y / _scale, w, h);
        int fontSize = Math.Max(1, (int)MathF.Round(ToolDrawerLayout.ModuleButtonFontSize / _scale));
        // The column was measured from this very title, so it fits without an ellipsis; the fit stays as a last
        // resort because the label wraps to a truncated second line otherwise.
        control.Label.text = ToolDrawerLayout.FitSingleLine(ToolboxService.Limit(module.Title, 40), fontSize,
            Math.Max(1, w - inset * 2));
        control.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
        control.Label.fontSize = fontSize;
        control.Label.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
        ToolTheme.CenterButtonLabel(control.Label, w, h, inset);
        control.Key = "module:" + module.Id;
        control.Action = action;
        control.Enabled = true;
        // Already physical pixels: the guard publishes it unchanged, unlike a panel control.
        control.Hit = bounds;
        bool hover = PointerOver(control, physical: true);
        control.Label.color = OfficialButtonSkin.Apply(control.Image, w, h, true, selected, hover,
            hover && _pressedKey == control.Key && UnityEngine.Input.GetMouseButton(0),
            decorated: false, primary: false, navigation: true);
        _active.Add(control);
    }

    /// <summary>
    /// Draws one entry row of a multi-entry module. Rows are drawn in the rail's own physical units, exactly like
    /// the module buttons, so the tree's first two levels are stable and only the panel appears and disappears.
    /// </summary>
    private void LayoutEntry(Control control, ToolEntry entry, ToolInputRect bounds, bool selected, Action action)
    {
        if (!bounds.IsValid) { control.GameObject.SetActive(false); return; }
        control.GameObject.SetActive(true);
        float w = bounds.Width / _scale;
        float h = bounds.Height / _scale;
        float inset = ToolDrawerLayout.ModuleButtonLabelInset / _scale;
        SetRect(control.Rect, bounds.X / _scale, bounds.Y / _scale, w, h);
        int fontSize = Math.Max(1, (int)MathF.Round(ToolDrawerLayout.ModuleButtonFontSize / _scale));
        control.Label.text = ToolDrawerLayout.FitSingleLine(ToolboxService.Limit(entry.Title, 60), fontSize,
            Math.Max(1, w - inset * 2));
        control.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
        control.Label.fontSize = fontSize;
        control.Label.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
        ToolTheme.CenterButtonLabel(control.Label, w, h, inset);
        control.Key = "entry:" + entry.Id;
        control.Action = action;
        control.Enabled = true;
        control.Hit = bounds;
        bool hover = PointerOver(control, physical: true);
        control.Label.color = OfficialButtonSkin.Apply(control.Image, w, h, true, selected, hover,
            hover && _pressedKey == control.Key && UnityEngine.Input.GetMouseButton(0),
            decorated: false, primary: false, navigation: true);
        _active.Add(control);
    }

    private bool PointerOver(Control control, bool physical)
    {
        var mouse = UnityEngine.Input.mousePosition;
        return control.Hit.Contains(physical ? mouse.x : mouse.x / _scale,
            physical ? mouse.y : mouse.y / _scale);
    }

    /// <summary>
    /// Draws one pooled control: rectangle, label, hit area and plate. <paramref name="face"/> names an optional
    /// button face (see <see cref="ToolButtonSkins"/>); the official one is used only when its artwork is actually
    /// available, and every other case keeps the shared plate.
    /// </summary>
    private void Layout(Control control, string key, string label, float x, float y, float w, float h,
        bool enabled, bool highlighted, Action action, bool panelRelative = true, string skin = "button", string? face = null)
    {
        control.GameObject.SetActive(true);
        SetRect(control.Rect, x, y, w, h);
        SetRect(control.Label.GetComponent<RectTransform>(), 9, 0, Math.Max(1, w - 18), h);
        // A glyph that is the whole label — the panel's × and the card's « — is drawn bigger than a word.
        bool glyph = label is "×" or "«";
        int fontSize = glyph ? 22 : 17;
        control.Label.text = glyph ? label
            : ToolDrawerLayout.FitSingleLine(ToolboxService.Limit(label, 200), fontSize, Math.Max(1, w - 22));
        control.Label.alignment = TextAnchor.MiddleCenter;
        control.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
        control.Label.fontSize = fontSize;
        control.Key = key;
        control.Action = action;
        control.Enabled = enabled;
        control.Hit = new ToolInputRect(x + (panelRelative ? _panelX : 0), y + (panelRelative ? _panelY : 0), w, h);
        string useSkin = !enabled ? "button.disabled" : highlighted ? "button.selected" : skin;
        bool button = key != "close" && key != "module-collapse" && key != "search"
            && !key.StartsWith("item:", StringComparison.Ordinal);
        if (button)
        {
            ToolTheme.CenterButtonLabel(control.Label, w, h, 9f);
            bool hover = PointerOver(control, physical: !panelRelative);
            bool primary = skin == "button.primary" || string.Equals(face, ToolButtonSkins.Official, StringComparison.Ordinal);
            control.Label.color = OfficialButtonSkin.Apply(control.Image, w, h, enabled, highlighted, hover,
                hover && _pressedKey == key && UnityEngine.Input.GetMouseButton(0),
                primary || skin == "button.undo", primary, navigation: false);
        }
        else
        {
            control.Image.enabled = true;
            control.Image.pixelsPerUnitMultiplier = 1f;
            control.Label.color = ToolTheme.Foreground(useSkin);
            if (!ToolTheme.Apply(control.Image, useSkin)) control.Image.color = ToolTheme.Native(ToolPalette.ForKey(useSkin).Background);
        }
        // The two coordinate spaces are kept in separate lists: a panel rectangle is measured in canvas units
        // and a rail or entry rectangle in physical pixels, and searching them as one list made the hit test
        // depend on the canvas scale.
        (panelRelative ? _panelActive : _active).Add(control);
    }

    private static readonly Color DarkText = ToolTheme.Native(ToolPalette.Text);

    private (GameObject GameObject, RectTransform Rect, Image Image) CreateImage(string name, Transform parent, string skin)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>(); rect.SetParent(parent, false);
        var image = go.AddComponent<Image>(); image.raycastTarget = false;
        image.color = ToolTheme.Native(ToolPalette.ForKey(skin).Background); ToolTheme.Apply(image, skin);
        return (go, rect, image);
    }

    private Control CreateControl(string name, Transform parent)
    {
        var image = CreateImage(name, parent, "button");
        return new Control(image.GameObject, image.Rect, image.Image, CreateText(name + "Label", image.Rect, "", 18, TextAnchor.MiddleCenter));
    }

    private Text CreateText(string name, Transform parent, string value, int size, TextAnchor alignment)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>(); rect.SetParent(parent, false);
        var text = go.AddComponent<Text>(); text.font = _font; text.text = value; text.fontSize = size;
        text.color = DarkText; text.alignment = alignment; text.supportRichText = false; text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private static void LayoutText(Text text, float x, float y, float w, float h) => SetRect(text.GetComponent<RectTransform>(), x, y, w, h);

    private static void SetRect(RectTransform rect, float x, float y, float w, float h)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.zero; rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(w, h); rect.localScale = Vector3.one;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetInteraction();
        // A page that built native state for its panel is told it is gone, so throwing the toolbox away cannot
        // leave an editor canvas behind.
        SyncHostedLifecycle(null);
        _region.Dispose();
        SetIme(false);
        Object.Destroy(_canvasObject);
    }

    private sealed class Control
    {
        internal readonly GameObject GameObject;
        internal readonly RectTransform Rect;
        internal readonly Image Image;
        internal readonly Text Label;
        internal string Key = "";
        internal Action? Action;
        internal bool Enabled;
        internal ToolInputRect Hit;
        internal Control(GameObject gameObject, RectTransform rect, Image image, Text label)
        { GameObject = gameObject; Rect = rect; Image = image; Label = label; }
    }
}
