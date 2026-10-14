using Rukari.Lib.Tools;
using Rukari.Lib.Runtime.Editor;
using Rukari.Lib.Runtime.Settings;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>Lifecycle entry point called by the owning runtime plugin, on the game main thread.</summary>
public static class ToolsHost
{
    private static ToolInputService? _input;
    private static ToolboxService? _toolbox;
    private static IDisposable? _inputRegistration;
    private static IDisposable? _toolboxRegistration;
    private static IDisposable? _windowRegistration;
    private static IDisposable? _savePage;

    /// <summary>Registers the shared tools only if the single native input guard was installed successfully.</summary>
    /// <param name="skipInputGuards">
    /// Android/PE bisect switch (docs/50): when true the native input guard is not installed and the shared toolbox is
    /// left unregistered, exactly as it would be if the guard had failed. Default false keeps a normal install intact.
    /// </param>
    /// <param name="takeOverKeyboardAndIme">
    /// False when the user asked the library to leave the keyboard and the engine's input-method switch alone
    /// (<c>[Input] TakeOverKeyboardAndIme</c>). The mouse guard, the toolbox and every tool page keep working; only
    /// NGUI's keyboard entry points and composition stay with the game. Default true keeps a normal install intact.
    /// </param>
    public static void Initialize(IModRuntime runtime, Action<string> log, bool skipInputGuards = false,
        bool takeOverKeyboardAndIme = true)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        if (!runtime.IsMainThread) throw new InvalidOperationException("Tool initialization requires the game main thread.");
        if (_input is not null) return;
        var input = new ToolInputService(log, takeOverKeyboardAndIme);
        if (skipInputGuards)
        {
            input.Dispose();
            log("Shared toolbox disabled: android-diagnostics SkipInputGuards is on.");
            return;
        }
        if (!input.Install()) { input.Dispose(); log("Shared toolbox disabled: safe mouse ownership is unavailable."); return; }
        ToolTheme.Initialize(log);
        RailIconPainter.Initialize(log);
        AtlasEmblemSource.Initialize(log);
        OfficialButtonSkin.Initialize(log);
        EditorWorkspaceContext.Initialize(log);
        var toolbox = new ToolboxService(input, log);
        var inputResult = runtime.RegisterService<IToolInputService>("rukari.lib.runtime", input,
            new CapabilityInfo(ToolCapabilities.Input, "rukari.lib.runtime", "0.2.0", CapabilityLevel.Experimental,
                "One zero-argument UICamera mouse guard for managed screen rectangles; runtime validation pending for this build."));
        if (!inputResult.Success)
        {
            toolbox.Dispose(); input.Dispose(); OfficialButtonSkin.Shutdown(); ToolTheme.Shutdown(); EditorWorkspaceContext.Shutdown();
            log(inputResult.Error!.Message); return;
        }
        var toolboxResult = runtime.RegisterService<IToolboxService>("rukari.lib.runtime", toolbox,
            new CapabilityInfo(ToolCapabilities.Toolbox, "rukari.lib.runtime", "0.2.0", CapabilityLevel.Experimental,
                "Shared node-scoped light drawer; managed page snapshots and actions; providers retain all feature logic."));
        if (!toolboxResult.Success)
        {
            inputResult.Value.Dispose(); toolbox.Dispose(); input.Dispose(); OfficialButtonSkin.Shutdown(); ToolTheme.Shutdown(); EditorWorkspaceContext.Shutdown();
            log(toolboxResult.Error!.Message); return;
        }
        // Windows are published as their own capability on the SAME service object: a mod that already found the
        // toolbox can ask it for this interface without a second lookup and a second registration lifetime.
        var windowResult = runtime.RegisterService<IToolWindowService>("rukari.lib.runtime", toolbox,
            new CapabilityInfo(ToolCapabilities.Windows, "rukari.lib.runtime", "0.2.0", CapabilityLevel.Experimental,
                "Standalone movable tool windows; the shared frame, theme and input ownership on a surface the user places."));
        if (!windowResult.Success)
        {
            toolboxResult.Value.Dispose(); inputResult.Value.Dispose(); toolbox.Dispose(); input.Dispose();
            OfficialButtonSkin.Shutdown(); ToolTheme.Shutdown(); EditorWorkspaceContext.Shutdown();
            log(windowResult.Error!.Message); return;
        }
        _input = input;
        _toolbox = toolbox;
        _inputRegistration = inputResult.Value;
        _toolboxRegistration = toolboxResult.Value;
        _windowRegistration = windowResult.Value;
        log("Shared toolbox and mouse ownership services registered; tool pages are supplied by enabled mods.");
        // The lib's own page, so saving does not depend on which feature packages are enabled.
        _savePage?.Dispose();
        _savePage = EditorSaveToolPage.Install(toolbox, runtime, log);
        try { SettingsHost.Initialize(runtime, input, log); }
        catch (Exception ex)
        {
            SettingsHost.Shutdown();
            log("Global mod settings unavailable; node-scoped tools remain available: " + ex.Message);
        }
    }

    /// <summary>Updates the renderer from the existing runtime behaviour; no additional native behaviour is injected.</summary>
    public static void Tick()
    {
        _input?.BeginFrame();
        SettingsHost.Tick();
        _toolbox?.Tick();
    }

    /// <summary>Releases pages, UI objects, registrations and the shared native patch on the game main thread.</summary>
    public static void Shutdown()
    {
        SettingsHost.Shutdown();
        _windowRegistration?.Dispose(); _windowRegistration = null;
        _toolboxRegistration?.Dispose(); _toolboxRegistration = null;
        _inputRegistration?.Dispose(); _inputRegistration = null;
        _toolbox?.Dispose(); _toolbox = null;
        _input?.Dispose(); _input = null;
        OfficialButtonSkin.Shutdown();
        ToolTheme.Shutdown();
        RailIconPainter.Shutdown();
        AtlasEmblemSource.Shutdown();
        EditorWorkspaceContext.Shutdown();
    }
}

internal sealed class ToolboxService : IToolboxService, IToolWindowService, IDisposable
{
    internal sealed class Page : IDisposable
    {
        private readonly ToolboxService _owner;
        private int _disposed;
        internal readonly string OwnerId;
        internal readonly string Id;
        internal readonly string Title;
        internal readonly string Icon;
        /// <summary>Snapshot reader for an ordinary page; null for a hosted page.</summary>
        internal readonly Func<ToolPageSnapshot>? Read;
        /// <summary>Hosted content for a page that draws itself; null for an ordinary page.</summary>
        internal readonly IToolPanelContent? Content;
        /// <summary>Action handler for an ordinary page; null for a hosted page, which owns its own actions.</summary>
        internal readonly Action<ToolAction>? Action;
        /// <summary>
        /// True when this page draws in its own standalone window instead of the modal drawer sheet. It still
        /// appears in its module's entries column, so the rail stays the one way into every tool.
        /// </summary>
        internal readonly bool Window;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal Page(ToolboxService owner, string ownerId, string id, string title, string icon,
            Func<ToolPageSnapshot>? read, Action<ToolAction>? action, IToolPanelContent? content, bool window)
        { _owner = owner; OwnerId = ownerId; Id = id; Title = title; Icon = icon; Read = read; Action = action; Content = content; Window = window; }
        public void Dispose()
        {
            // A lease may be released from any thread, so this only marks the page gone. The renderer notices on its
            // next Tick and performs the native teardown there.
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner._dirty = true;
        }
    }

    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly List<Page> _pages = new();
    /// <summary>
    /// Where the user put each standalone window, for as long as this library lives. Session-scoped: a position is
    /// worth keeping while the game runs, and writing it to disk would need a file this library does not own.
    /// </summary>
    private readonly ToolWindowPlacementStore _placements = new();
    /// <summary>
    /// Who is in front. Windows and the drawer both consult it, because one press must reach exactly one surface.
    /// </summary>
    private readonly ToolWindowStack _windowStack = new();
    /// <summary>
    /// One live host per window-backed page, created when that page is registered, so a mod learns immediately
    /// whether a window could be built at all and can fall back to a drawer page when it could not.
    /// </summary>
    private readonly Dictionary<string, ToolPanelWindow> _windowHosts = new(StringComparer.Ordinal);
    private readonly ToolInputService _input;
    private readonly Action<string> _log;
    /// <summary>
    /// The single source of truth for how far the drawer is open: 0 = rail only, 1 = the open module's entries,
    /// 2 = a leaf panel as well. Keeping the depth here instead of in the renderer is what makes "one level at a
    /// time" a testable rule rather than a drawing coincidence.
    /// </summary>
    private readonly ToolTreeState _tree = new();
    private ToolboxDrawer? _drawer;
    private Page? _selected;
    private ToolPageSnapshot _snapshot = EmptySnapshot;
    private bool _stopped;
    private bool _uiFailed;
    private volatile bool _dirty = true;
    private int _frames;
    internal static readonly ToolPageSnapshot EmptySnapshot = new("暂无可用工具", Array.Empty<ToolButton>(), Status: "启用功能 Mod 后，工具会出现在这里。");

    internal ToolboxService(ToolInputService input, Action<string> log) { _input = input; _log = log; }

    public bool IsOpen => _tree.Depth == 2 && !_stopped && !_uiFailed && _pages.Any(p => !p.Disposed)
        && EditorWorkspaceContext.IsToolWorkspaceVisible && !SettingsHost.IsOpen;

    public ModResult<IDisposable> RegisterPage(string ownerId, string pageId, string title,
        Func<ToolPageSnapshot> snapshot, Action<ToolAction> action) =>
        RegisterPage(ownerId, pageId, title, snapshot, action, null);

    public ModResult<IDisposable> RegisterPage(string ownerId, string pageId, string title,
        Func<ToolPageSnapshot> snapshot, Action<ToolAction> action, string? iconId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(action);
        return Register(ownerId, pageId, title, iconId, snapshot, action, null, window: false);
    }

    public ModResult<IDisposable> RegisterHostedPage(string ownerId, string pageId, string title,
        IToolPanelContent content, string? iconId)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Register(ownerId, pageId, title, iconId, null, null, content, window: false);
    }

    /// <summary>
    /// Registers a page that draws itself in its own movable window instead of the modal drawer sheet. The window is
    /// built here rather than on first use, so a library that cannot build one says so now and the caller can fall
    /// back to <see cref="RegisterHostedPage"/> instead of discovering it when the user clicks the entry.
    /// </summary>
    /// <summary>
    /// Most windows that may be registered at once. It is a drawing limit, not an arbitrary one: windows are stacked
    /// by one canvas sorting order each, and the table that decides which window owns a press has to agree with what
    /// is actually drawn on top. Past this count the two would disagree, so registration fails instead of letting a
    /// click reach a window the user cannot see.
    /// </summary>
    private const int MaximumWindows = 10;

    public ModResult<IDisposable> RegisterWindow(string ownerId, string windowId, string title,
        IToolPanelContent content, string? iconId)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Environment.CurrentManagedThreadId != _threadId)
            return ModResult<IDisposable>.Fail(ModErrorCode.WrongThread, "Window registration requires the main thread.");
        if (_windowHosts.Count >= MaximumWindows)
            return ModResult<IDisposable>.Fail(ModErrorCode.Busy,
                $"At most {MaximumWindows} tool windows can be registered at once.");
        ModResult<IDisposable> registered = Register(ownerId, windowId, title, iconId, null, null, content, window: true);
        if (!registered.Success) return registered;
        var page = (Page)registered.Value;
        // The same window id can be registered again in the frame its old lease was released: that host still holds
        // the id's input-region key until the next Tick, and the new one would fail to register and be discarded.
        if (_windowHosts.TryGetValue(windowId, out ToolPanelWindow? previous))
        {
            _windowHosts.Remove(windowId);
            try { previous.Dispose(); }
            catch (Exception ex) { _log($"Tool window {windowId} cleanup failed before re-registration: {ex.Message}"); }
        }
        try
        {
            _windowHosts[windowId] = new ToolPanelWindow(_input, windowId, title, content, _placements, _windowStack, _log);
        }
        catch (Exception ex)
        {
            page.Dispose();
            _pages.Remove(page);
            _log($"Tool window {windowId} could not be created: {ex.Message}");
            return ModResult<IDisposable>.Fail(ModErrorCode.NotReady, "The tool window could not be created.");
        }
        return registered;
    }

    public ModResult<bool> Show(string windowId)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            return ModResult<bool>.Fail(ModErrorCode.WrongThread, "Opening a tool window requires the main thread.");
        if (_stopped || _uiFailed) return ModResult<bool>.Fail(ModErrorCode.NotReady, "The toolbox renderer is unavailable.");
        if (!_windowHosts.TryGetValue(windowId ?? string.Empty, out ToolPanelWindow? window))
            return ModResult<bool>.Fail(ModErrorCode.NotFound, "The tool window is not registered.");
        if (!EditorWorkspaceContext.IsToolWorkspaceVisible || SettingsHost.IsOpen)
            return ModResult<bool>.Fail(ModErrorCode.NotReady, "请返回项目内的台词编辑界面，并关闭官方选择窗口。");
        window.Requested = true;
        return ModResult<bool>.Ok(true);
    }

    public void Hide(string windowId)
    {
        RequireThread();
        if (windowId is not null && _windowHosts.TryGetValue(windowId, out ToolPanelWindow? window)) window.Requested = false;
    }

    public bool IsWindowOpen(string windowId) =>
        windowId is not null && _windowHosts.TryGetValue(windowId, out ToolPanelWindow? window) && window.IsVisible;

    public ModResult<bool> Toggle(string windowId)
    {
        if (IsWindowOpen(windowId))
        {
            Hide(windowId);
            return ModResult<bool>.Ok(false);
        }
        return Show(windowId);
    }

    public bool ResetPlacement(string windowId)
    {
        bool forgotten = _placements.Reset(windowId);
        // A live window keeps its own resolved placement, so it has to be told: without this the remembered position
        // was dropped while the window on screen stayed exactly where the user left it.
        if (windowId is not null && _windowHosts.TryGetValue(windowId, out ToolPanelWindow? window)) window.ResetPlacement();
        return forgotten;
    }

    private ModResult<IDisposable> Register(string ownerId, string pageId, string title, string? iconId,
        Func<ToolPageSnapshot>? snapshot, Action<ToolAction>? action, IToolPanelContent? content, bool window)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            return ModResult<IDisposable>.Fail(ModErrorCode.WrongThread, "Page registration requires the main thread.");
        if (_stopped) return ModResult<IDisposable>.Fail(ModErrorCode.NotReady, "The toolbox has stopped.");
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(title)
            || pageId.Length > 160 || title.Length > 80)
            return ModResult<IDisposable>.Fail(ModErrorCode.InvalidArgument, "A page owner, unique ID and title are required.");
        _pages.RemoveAll(p => p.Disposed);
        if (_pages.Any(p => StringComparer.Ordinal.Equals(p.Id, pageId)))
            return ModResult<IDisposable>.Fail(ModErrorCode.Conflict, "The tool page ID is already registered.");
        if (_pages.Count >= 16)
            return ModResult<IDisposable>.Fail(ModErrorCode.Busy, "The toolbox page limit was reached.");
        var page = new Page(this, ownerId, pageId, title, (iconId ?? "").Trim(), snapshot, action, content, window);
        _pages.Add(page);
        // A window page is never the drawer's selected page: the drawer draws a sheet for whatever is selected, and
        // this page is already on screen in a window of its own.
        if (!window) _selected ??= page;
        _dirty = true;
        return ModResult<IDisposable>.Ok(page);
    }

    /// <summary>
    /// One permanent rail button per enabled mod, in registration order. Pages sharing an ownerId collapse
    /// into that single button, so a future feature only has to register a page to get its own sidebar entry.
    /// </summary>
    internal IReadOnlyList<ToolModule> Modules()
    {
        var modules = new List<ToolModule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Page page in _pages)
        {
            if (page.Disposed || !seen.Add(page.OwnerId)) continue;
            modules.Add(new ToolModule(page.OwnerId, page.Title, page.Icon));
        }
        return modules;
    }

    /// <summary>
    /// The entries column: one entry per page of the open module, in registration order. A module with a single
    /// entry is opened by its own button, so it publishes no column at all and its panel is the second press;
    /// a module with several entries gets the column a user can choose from.
    /// </summary>
    internal IReadOnlyList<ToolEntry> EntriesOf(string? ownerId)
    {
        if (ownerId == null) return Array.Empty<ToolEntry>();
        var entries = new List<ToolEntry>();
        foreach (Page page in _pages)
        {
            if (page.Disposed || !StringComparer.Ordinal.Equals(page.OwnerId, ownerId)) continue;
            entries.Add(new ToolEntry(page.Id, page.Title, page.Icon));
        }
        return entries;
    }

    /// <summary>
    /// What the drawer should draw as the entries column: nothing for a single-entry module, whose button opens
    /// its panel directly.
    /// </summary>
    internal static IReadOnlyList<ToolEntry> ColumnEntries(IReadOnlyList<ToolEntry> entries) =>
        entries.Count > 1 ? entries : Array.Empty<ToolEntry>();

    /// <summary>Index of the open entry inside the open module's column, or -1 when no leaf is open.</summary>
    internal int OpenEntryIndex(IReadOnlyList<ToolEntry> entries)
    {
        if (_tree.OpenEntryId == null) return -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (StringComparer.Ordinal.Equals(entries[i].Id, _tree.OpenEntryId)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Index of a module in the rail, or -1 when that module is not registered. The drawer highlights exactly the
    /// module that is OPEN, never one that was merely selected earlier: a column that comes up with a button
    /// already lit both claims a press the user never made and makes the next press on that button a close.
    /// </summary>
    internal int ModuleIndexOf(string? ownerId)
    {
        if (ownerId == null) return -1;
        int index = 0;
        foreach (ToolModule module in Modules())
        {
            if (StringComparer.Ordinal.Equals(module.Id, ownerId)) return index;
            index++;
        }
        return -1;
    }

    /// <summary>Selects a module's first drawer page, which is what a rail button click asks for.</summary>
    internal void SelectModule(string ownerId)
    {
        // Never a window page: the drawer draws a sheet for whatever is selected, and that page is on screen
        // somewhere else. A module that publishes only windows therefore leaves the selection where it was.
        Page? first = _pages.FirstOrDefault(p => !p.Disposed && !p.Window
            && StringComparer.Ordinal.Equals(p.OwnerId, ownerId));
        if (first is not null) Select(first);
    }

    /// <summary>Notifies the toolbox that the currently displayed module changed.</summary>
    internal void NotifyModuleChanged() => _dirty = true;

    public ModResult<bool> Open(string? pageId = null)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            return ModResult<bool>.Fail(ModErrorCode.WrongThread, "Opening a tool requires the main thread.");
        if (_stopped || _uiFailed) return ModResult<bool>.Fail(ModErrorCode.NotReady, "The toolbox renderer is unavailable.");
        if (!EditorWorkspaceContext.IsToolWorkspaceVisible || SettingsHost.IsOpen)
        {
            HideOutsideWorkspace();
            return ModResult<bool>.Fail(ModErrorCode.NotReady, "请返回项目内的台词编辑界面，并关闭官方选择窗口。");
        }
        if (pageId is not null)
        {
            Page? page = _pages.FirstOrDefault(p => !p.Disposed && p.Id == pageId);
            if (page is null) return ModResult<bool>.Fail(ModErrorCode.NotFound, "The tool page is not registered.");
            Select(page);
        }
        // A mod that asks for a page programmatically wants the leaf itself, so the tree opens both levels at
        // once; the rail icon is what opens one level at a time.
        if (_selected is not null && !_selected.Disposed) _tree.OpenEntry(_selected.OwnerId, _selected.Id);
        _drawer?.ResetInteraction();
        _dirty = true;
        return ModResult<bool>.Ok(true);
    }

    public void Close()
    {
        RequireThread();
        _tree.CollapseAll();
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
    }

    /// <summary>
    /// One module button: it shows that module's panel. A module that publishes a single entry opens its panel
    /// straight away, because a one-row column would be a press that says nothing; a module with several entries
    /// reveals the column the user chooses from. Pressing the button of the module that is already open walks the
    /// tree back one visible level, exactly like the close button, so no press can ever look like it did nothing.
    /// </summary>
    private void PressModule(string ownerId)
    {
        if (StringComparer.Ordinal.Equals(_tree.OpenModuleId, ownerId))
        {
            // A module whose single entry draws in a window has no column to walk back through and no sheet to
            // close, so its own button is what closes the window it opened.
            if (SingleWindowPage(ownerId) is { } open)
            {
                ToggleWindow(open.Id);
                return;
            }
            CloseOneLevel();
            return;
        }

        _tree.OpenModule(ownerId);
        SelectModule(ownerId);
        IReadOnlyList<ToolEntry> entries = EntriesOf(ownerId);
        // OpenEntry is what routes a window-backed entry to its window and leaves the tree at the column.
        if (entries.Count == 1) OpenEntry(entries[0].Id);
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
    }

    /// <summary>
    /// The module's only page, when that page draws in its own window, and null for every other module: one with
    /// several entries is entered through its column, and one with a sheet is closed by walking back a level.
    /// </summary>
    private Page? SingleWindowPage(string ownerId)
    {
        Page? found = null;
        foreach (Page page in _pages)
        {
            if (page.Disposed || !StringComparer.Ordinal.Equals(page.OwnerId, ownerId)) continue;
            if (found is not null) return null;
            found = page;
        }
        return found is { Window: true } ? found : null;
    }

    /// <summary>
    /// Opens one entry of the open module, which is the press that shows the panel.
    /// </summary>
    private void OpenEntry(string pageId)
    {
        Page? page = _pages.FirstOrDefault(p => !p.Disposed && StringComparer.Ordinal.Equals(p.Id, pageId));
        if (page is null)
        {
            // The provider unloaded between the redraw and the click, so walk back instead of drawing a panel
            // for a page that no longer exists.
            CloseOneLevel();
            return;
        }
        // A window-backed entry opens or closes its window, and the card it was reached through closes with it: the
        // window is the tool now, and a lingering column only stacks more chrome on the screen.
        if (page.Window)
        {
            ToggleWindow(page.Id);
            CloseOneLevel();
            return;
        }
        _tree.OpenEntry(page.OwnerId, page.Id);
        Select(page);
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
    }

    /// <summary>
    /// One entry of a module that draws in its own window: the window opens or closes and the tree stays where it
    /// is, so the entries column remains visible and a second tool can be opened from it without closing the first.
    /// </summary>
    private void ToggleWindow(string windowId)
    {
        ModResult<bool> result = Toggle(windowId);
        if (!result.Success) _log($"Tool window {windowId} could not be toggled: {result.Error?.Message}");
    }

    /// <summary>
    /// Panel to entries to module column to rail: one press, one visible level. What the panel's close button,
    /// Escape, and a second press on the open module's own button all ask for.
    ///
    /// Whether the open module draws an entries column decides how many levels it really has: a single-page
    /// module's button opens its panel directly, so its panel and its module close together instead of leaving a
    /// lit button over nothing.
    /// </summary>
    private void CloseOneLevel()
    {
        _tree.CollapseOneLevel(ColumnEntries(EntriesOf(_tree.OpenModuleId)).Count != 0);
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
    }

    /// <summary>
    /// The big icon: the way into the column, and out of every level at once. It deliberately selects nothing —
    /// the module the user presses is the only thing that opens a module.
    /// </summary>
    private void ToggleMaster()
    {
        if (_tree.Depth == 0 && _pages.Count == 0) return;
        _tree.PressRailIcon();
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
    }

    public void Refresh(string? pageId = null)
    {
        RequireThread();
        if (pageId is null || _selected?.Id == pageId) _dirty = true;
    }

    private void RequireThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Toolbox operations require the game main thread.");
    }

    private void Select(Page page)
    {
        _selected = page;
        _drawer?.ResetPage();
        _dirty = true;
    }

    internal void Tick()
    {
        if (_stopped || _uiFailed || Environment.CurrentManagedThreadId != _threadId) return;
        try
        {
            // Windows follow the same visibility rule as the drawer: they hide while an official selector covers
            // the editor and come back with it, keeping the place and the open/closed state the user left them in.
            bool allowed = EditorWorkspaceContext.IsToolWorkspaceVisible && !SettingsHost.IsOpen;
            if (!allowed) { HideOutsideWorkspace(); TickWindows(false); return; }
            _pages.RemoveAll(p => p.Disposed);
            CloseDeadWindows();
            TickWindows(true);
            if (_selected is null || _selected.Disposed) { _selected = _pages.FirstOrDefault(p => !p.Window); _dirty = true; _drawer?.ResetPage(); }
            // A module or entry whose provider unloaded cannot keep a column, let alone a panel, on screen.
            IReadOnlyList<ToolEntry> openEntries = EntriesOf(_tree.OpenModuleId);
            if (_tree.Synchronize(Modules(), openEntries)) _drawer?.BeginLevelChange();
            // Lazily create native UI only once at least one enabled feature supplies a page.
            if (_pages.Count == 0) { _tree.CollapseAll(); _drawer?.Hide(); return; }
            _drawer ??= new ToolboxDrawer(_input, _log) { CoveredByWindow = CoveredByWindow };
            if (_dirty || ++_frames % 15 == 0)
            {
                _dirty = false;
                ReadSnapshot();
            }
            IReadOnlyList<ToolEntry> columnEntries = ColumnEntries(openEntries);
            _drawer.Tick(_tree.Depth, Modules(), ModuleIndexOf(_tree.OpenModuleId), columnEntries,
                OpenEntryIndex(columnEntries), _selected, _selected?.Content, _snapshot, ToggleMaster, PressModule,
                OpenEntry, CloseOneLevel, InvokeAction);
        }
        catch (Exception ex)
        {
            StopRenderer(ex);
        }
    }

    private void HideOutsideWorkspace()
    {
        _tree.CollapseAll();
        _snapshot = EmptySnapshot;
        _dirty = true;
        try { _drawer?.Hide(); }
        catch (Exception ex) { StopRenderer(ex); }
    }

    /// <summary>Draws every live window. <paramref name="allowed"/> is false while the tool workspace is off screen.</summary>
    private void TickWindows(bool allowed)
    {
        if (_windowHosts.Count == 0) return;
        foreach (ToolPanelWindow window in _windowHosts.Values) window.Tick(allowed);
    }

    /// <summary>Whether a standalone window is in front at this physical screen point.</summary>
    private bool CoveredByWindow(float x, float y) => _windowStack.OwnerAt(x, y) is not null;

    /// <summary>
    /// Tears down the windows whose registration was released. A lease only marks its page gone, because it may be
    /// released from any thread; the native teardown happens here, on the game main thread.
    /// </summary>
    private void CloseDeadWindows()
    {
        if (_windowHosts.Count == 0) return;
        foreach (string id in _windowHosts.Keys.ToArray())
        {
            bool alive = false;
            foreach (Page page in _pages)
            {
                if (page.Disposed || !page.Window || !StringComparer.Ordinal.Equals(page.Id, id)) continue;
                alive = true;
                break;
            }
            if (alive) continue;
            ToolPanelWindow window = _windowHosts[id];
            _windowHosts.Remove(id);
            try { window.Dispose(); }
            catch (Exception ex) { _log($"Tool window {id} native cleanup failed after its lease was released: {ex.Message}"); }
        }
    }

    private void StopRenderer(Exception cause)
    {
        _uiFailed = true;
        _tree.CollapseAll();
        ToolboxDrawer? drawer = _drawer;
        _drawer = null;
        try { drawer?.Dispose(); }
        catch (Exception cleanup) { _log($"Shared toolbox native cleanup failed after input release: {cleanup.Message}"); }
        // A window owns a canvas and an input region of its own, and nothing would tick it once the service has
        // stopped, so it is torn down here rather than left on screen swallowing clicks for a dead renderer.
        foreach (ToolPanelWindow window in _windowHosts.Values)
        {
            try { window.Dispose(); }
            catch (Exception cleanup) { _log($"Tool window cleanup failed after the renderer stopped: {cleanup.Message}"); }
        }
        _windowHosts.Clear();
        _log($"Shared toolbox renderer stopped and released its input: {cause}");
    }

    private void ReadSnapshot()
    {
        if (_selected is null || _selected.Disposed) { _snapshot = EmptySnapshot; return; }
        // A hosted page draws its own content, so there is nothing to read and no stale panel state to keep.
        if (_selected.Content is not null) { _snapshot = EmptySnapshot; return; }
        try
        {
            ToolPageSnapshot value = _selected.Read?.Invoke()
                ?? throw new InvalidOperationException("The provider returned no page snapshot.");
            if (value.Buttons is null || value.Buttons.Count > 8 || (value.Items?.Count ?? 0) > 10000)
                throw new InvalidOperationException("A page supports at most 8 actions and 10,000 list rows.");
            var buttons = value.Buttons.ToArray();
            var items = value.Items?.ToArray() ?? Array.Empty<ToolListItem>();
            if (buttons.Any(b => b is null || string.IsNullOrWhiteSpace(b.Id)) || items.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id))
                || buttons.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != buttons.Length
                || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
                throw new InvalidOperationException("Button and row IDs must be nonempty and unique.");
            _snapshot = value with { Summary = Limit(value.Summary, 900), Status = Limit(value.Status, 600), Buttons = buttons, Items = value.Items is null ? null : items };
        }
        catch (Exception ex)
        {
            _snapshot = new ToolPageSnapshot("工具暂时无法读取当前状态", Array.Empty<ToolButton>(), Status: Limit(ex.Message, 500));
        }
    }

    private void InvokeAction(ToolAction action)
    {
        if (!EditorWorkspaceContext.IsToolWorkspaceVisible || SettingsHost.IsOpen) { HideOutsideWorkspace(); return; }
        Page? page = _selected;
        // A hosted page owns its own controls and therefore its own actions; there is nothing to dispatch.
        if (page is null || page.Disposed || page.Action is null) return;
        try { page.Action(action); }
        catch (Exception ex)
        {
            _log($"Tool page {page.Id} action failed: {ex.Message}");
            _snapshot = _snapshot with { Status = "操作未完成：" + Limit(ex.Message, 350) };
        }
        _dirty = true;
    }

    internal static string Limit(string? text, int maximum) => string.IsNullOrEmpty(text) ? "" : text.Length <= maximum ? text : text[..maximum] + "…";

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        foreach (Page page in _pages) page.Dispose();
        _pages.Clear();
        foreach (ToolPanelWindow window in _windowHosts.Values)
        {
            try { window.Dispose(); }
            catch (Exception ex) { _log($"Tool window cleanup failed: {ex.Message}"); }
        }
        _windowHosts.Clear();
        _placements.Clear();
        _windowStack.Clear();
        _drawer?.Dispose(); _drawer = null;
    }
}
