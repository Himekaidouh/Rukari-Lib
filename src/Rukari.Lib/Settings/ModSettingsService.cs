using Rukari.Lib.Tools;

namespace Rukari.Lib.Settings;

internal sealed class ModSettingsPage
{
    internal ModSettingsPage(string ownerId, string title, IToolPanelContent content)
    {
        OwnerId = ownerId;
        Title = title;
        Content = content;
    }

    internal string OwnerId { get; }
    internal string Title { get; }
    internal IToolPanelContent Content { get; }
}

// One main-thread state machine owns both registration and lifecycle. The native adapter never calls page
// lifecycle methods itself, so hiding the official settings parent cannot strand a third-party page on screen.
internal sealed class ModSettingsService : IModSettingsService, IDisposable
{
    private readonly Func<bool> _isMainThread;
    private readonly Func<ModResult<bool>> _showWindow;
    private readonly Action _hideWindow;
    private readonly Action<string, Exception>? _log;
    private readonly List<ModSettingsPage> _pages = new();
    private bool _stopped;
    private bool _busy;
    private bool _lifecycleActive;
    private bool _pageFailed;

    internal ModSettingsService(Func<bool> isMainThread, Func<ModResult<bool>> showWindow,
        Action hideWindow, Action<string, Exception>? log)
    {
        _isMainThread = isMainThread ?? throw new ArgumentNullException(nameof(isMainThread));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _hideWindow = hideWindow ?? throw new ArgumentNullException(nameof(hideWindow));
        _log = log;
    }

    public bool IsOpen { get; private set; }
    internal IReadOnlyList<ModSettingsPage> Pages => Array.AsReadOnly(_pages.ToArray());
    internal ModSettingsPage? SelectedPage { get; private set; }
    internal string? LastPageError { get; private set; }

    public ModResult<IDisposable> RegisterPage(string ownerId, string title, IToolPanelContent content)
    {
        ModError? error = CheckOperation();
        if (error is not null) return ModResult<IDisposable>.Fail(error);
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 160 || ownerId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl)
            || content is null)
            return ModResult<IDisposable>.Fail(ModErrorCode.InvalidArgument, "A bounded owner, title and content are required.");
        ownerId = ownerId.Trim();
        if (_pages.Any(page => string.Equals(page.OwnerId, ownerId, StringComparison.Ordinal)))
            return ModResult<IDisposable>.Fail(ModErrorCode.Conflict, "This owner already has a settings page.");
        if (_pages.Count >= 128)
            return ModResult<IDisposable>.Fail(ModErrorCode.Busy, "The settings registry is full.");
        var page = new ModSettingsPage(ownerId, title.Trim(), content);
        _pages.Add(page);
        return ModResult<IDisposable>.Ok(new PageLease(this, page));
    }

    public ModResult<bool> Open(string? ownerId)
    {
        ModError? error = CheckOperation();
        if (error is not null) return ModResult<bool>.Fail(error);
        ModSettingsPage? next = ownerId is null
            ? SelectedPage ?? _pages.FirstOrDefault()
            : _pages.FirstOrDefault(page => string.Equals(page.OwnerId, ownerId, StringComparison.Ordinal));
        if (ownerId is not null && next is null)
            return ModResult<bool>.Fail(ModErrorCode.NotFound, "The requested settings page is not registered.");

        _busy = true;
        try
        {
            ModResult<bool> visible;
            try { visible = _showWindow(); }
            catch (Exception exception)
            {
                Log("Settings window could not be shown.", exception);
                CloseCore();
                return ModResult<bool>.Fail(ModErrorCode.ProviderFailed, "The settings window could not be displayed.");
            }
            if (!visible.Success || !visible.Value)
            {
                CloseCore();
                return visible.Success
                    ? ModResult<bool>.Fail(ModErrorCode.NotReady, "The settings host is not visible.")
                    : ModResult<bool>.Fail(visible.Error!);
            }

            IsOpen = true;
            if (!ReferenceEquals(SelectedPage, next) || _pageFailed)
                SelectCore(next);
            return _pageFailed
                ? ModResult<bool>.Fail(ModErrorCode.ProviderFailed, LastPageError!)
                : ModResult<bool>.Ok(true);
        }
        finally { _busy = false; }
    }

    public void Close()
    {
        RequireMainThread();
        if (_stopped) return;
        RequireIdle();
        if (!IsOpen && SelectedPage is null) return;
        _busy = true;
        try { CloseCore(); }
        finally { _busy = false; }
    }

    internal ModResult<bool> DrawSelected(IToolPanelSurface surface)
    {
        ModError? error = CheckOperation();
        if (error is not null) return ModResult<bool>.Fail(error);
        if (surface is null)
            return ModResult<bool>.Fail(ModErrorCode.InvalidArgument, "A drawing surface is required.");
        if (!IsOpen || SelectedPage is null) return ModResult<bool>.Ok(false);
        if (_pageFailed) return ModResult<bool>.Fail(ModErrorCode.ProviderFailed, LastPageError!);
        _busy = true;
        try
        {
            SelectedPage.Content.Draw(surface);
            return ModResult<bool>.Ok(true);
        }
        catch (Exception exception)
        {
            FailPage("Settings page draw failed.", exception);
            return ModResult<bool>.Fail(ModErrorCode.ProviderFailed, LastPageError!);
        }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        RequireMainThread();
        if (_stopped) return;
        RequireIdle();
        _busy = true;
        _stopped = true;
        try
        {
            CloseCore();
            _pages.Clear();
        }
        finally { _busy = false; }
    }

    private void Remove(ModSettingsPage page)
    {
        RequireMainThread();
        if (_stopped || !_pages.Contains(page)) return;
        RequireIdle();
        _busy = true;
        try
        {
            _pages.Remove(page);
            if (ReferenceEquals(SelectedPage, page))
                SelectCore(IsOpen ? _pages.FirstOrDefault() : null);
        }
        finally { _busy = false; }
    }

    private void SelectCore(ModSettingsPage? next)
    {
        HideCurrent();
        SelectedPage = next;
        _pageFailed = false;
        LastPageError = null;
        if (next?.Content is not IToolPanelLifecycle lifecycle) return;
        // Set before invoking: even a partially successful OnShown receives exactly one cleanup attempt.
        _lifecycleActive = true;
        try { lifecycle.OnShown(); }
        catch (Exception exception) { FailPage("Settings page show failed.", exception); }
    }

    private void HideCurrent()
    {
        if (!_lifecycleActive) return;
        _lifecycleActive = false;
        try { ((IToolPanelLifecycle)SelectedPage!.Content).OnHidden(); }
        catch (Exception exception) { Log("Settings page hide failed.", exception); }
    }

    private void FailPage(string detail, Exception exception)
    {
        _pageFailed = true;
        LastPageError = "该模组的设置暂时无法显示，请切换栏目后重试。";
        Log(detail, exception);
        HideCurrent();
    }

    private void CloseCore()
    {
        bool wasOpen = IsOpen;
        IsOpen = false;
        HideCurrent();
        SelectedPage = null;
        LastPageError = null;
        _pageFailed = false;
        // Also hide after a failed show: the adapter may have created only part of its window before throwing.
        try { _hideWindow(); }
        catch (Exception exception) { Log(wasOpen ? "Settings window hide failed." : "Settings partial window cleanup failed.", exception); }
    }

    private ModError? CheckOperation()
    {
        if (!_isMainThread()) return new(ModErrorCode.WrongThread, "Settings operations require the main thread.");
        if (_stopped) return new(ModErrorCode.NotReady, "The settings service has stopped.");
        return _busy ? new(ModErrorCode.Busy, "A settings callback is already running.") : null;
    }

    private void RequireMainThread()
    {
        if (!_isMainThread()) throw new InvalidOperationException("Settings disposal and closing require the main thread.");
    }

    private void RequireIdle()
    {
        if (_busy) throw new InvalidOperationException("Settings callbacks cannot mutate their own registration or window.");
    }

    private void Log(string message, Exception exception)
    {
        try { _log?.Invoke($"{message} Owner={SelectedPage?.OwnerId ?? "none"}", exception); }
        catch { /* Diagnostics must not disable other settings pages. */ }
    }

    private sealed class PageLease : IDisposable
    {
        private ModSettingsService? _owner;
        private readonly ModSettingsPage _page;

        internal PageLease(ModSettingsService owner, ModSettingsPage page) { _owner = owner; _page = page; }

        public void Dispose()
        {
            if (_owner is null) return;
            _owner.Remove(_page);
            _owner = null;
        }
    }
}
