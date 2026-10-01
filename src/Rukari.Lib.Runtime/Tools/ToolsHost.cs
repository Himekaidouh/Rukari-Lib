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
    private static IDisposable? _savePage;

    /// <summary>Registers the shared tools only if the single native input guard was installed successfully.</summary>
    public static void Initialize(IModRuntime runtime, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        if (!runtime.IsMainThread) throw new InvalidOperationException("Tool initialization requires the game main thread.");
        if (_input is not null) return;
        var input = new ToolInputService(log);
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
        _input = input;
        _toolbox = toolbox;
        _inputRegistration = inputResult.Value;
        _toolboxRegistration = toolboxResult.Value;
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

internal sealed class ToolboxService : IToolboxService, IDisposable
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
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal Page(ToolboxService owner, string ownerId, string id, string title, string icon,
            Func<ToolPageSnapshot>? read, Action<ToolAction>? action, IToolPanelContent? content)
        { _owner = owner; OwnerId = ownerId; Id = id; Title = title; Icon = icon; Read = read; Action = action; Content = content; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner._dirty = true;
        }
    }

    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly List<Page> _pages = new();
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
        return Register(ownerId, pageId, title, iconId, snapshot, action, null);
    }

    public ModResult<IDisposable> RegisterHostedPage(string ownerId, string pageId, string title,
        IToolPanelContent content, string? iconId)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Register(ownerId, pageId, title, iconId, null, null, content);
    }

    private ModResult<IDisposable> Register(string ownerId, string pageId, string title, string? iconId,
        Func<ToolPageSnapshot>? snapshot, Action<ToolAction>? action, IToolPanelContent? content)
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
        var page = new Page(this, ownerId, pageId, title, (iconId ?? "").Trim(), snapshot, action, content);
        _pages.Add(page);
        _selected ??= page;
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

    /// <summary>Selects a module's first page, which is what a rail button click asks for.</summary>
    internal void SelectModule(string ownerId)
    {
        Page? first = _pages.FirstOrDefault(p => !p.Disposed && StringComparer.Ordinal.Equals(p.OwnerId, ownerId));
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
            CloseOneLevel();
            return;
        }

        _tree.OpenModule(ownerId);
        SelectModule(ownerId);
        IReadOnlyList<ToolEntry> entries = EntriesOf(ownerId);
        if (entries.Count == 1) _tree.OpenEntry(ownerId, entries[0].Id);
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
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
        _tree.OpenEntry(page.OwnerId, page.Id);
        Select(page);
        try { _drawer?.BeginLevelChange(); }
        catch (Exception ex) { StopRenderer(ex); }
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
            if (!EditorWorkspaceContext.IsToolWorkspaceVisible || SettingsHost.IsOpen) { HideOutsideWorkspace(); return; }
            _pages.RemoveAll(p => p.Disposed);
            if (_selected is null || _selected.Disposed) { _selected = _pages.FirstOrDefault(); _dirty = true; _drawer?.ResetPage(); }
            // A module or entry whose provider unloaded cannot keep a column, let alone a panel, on screen.
            IReadOnlyList<ToolEntry> openEntries = EntriesOf(_tree.OpenModuleId);
            if (_tree.Synchronize(Modules(), openEntries)) _drawer?.BeginLevelChange();
            // Lazily create native UI only once at least one enabled feature supplies a page.
            if (_pages.Count == 0) { _tree.CollapseAll(); _drawer?.Hide(); return; }
            _drawer ??= new ToolboxDrawer(_input, _log);
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

    private void StopRenderer(Exception cause)
    {
        _uiFailed = true;
        _tree.CollapseAll();
        ToolboxDrawer? drawer = _drawer;
        _drawer = null;
        try { drawer?.Dispose(); }
        catch (Exception cleanup) { _log($"Shared toolbox native cleanup failed after input release: {cleanup.Message}"); }
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
        _drawer?.Dispose(); _drawer = null;
    }
}
