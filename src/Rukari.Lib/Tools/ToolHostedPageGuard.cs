namespace Rukari.Lib.Tools;

/// <summary>
/// Contains provider failures inside one hosted registration's showing.
/// Native rendering stays outside this guard. A failed provider is not called
/// again until the user leaves that registration and returns.
/// </summary>
internal sealed class ToolHostedPageGuard
{
    private readonly Action<string> _log;
    private readonly ToolPanelSession _session = new();
    private object? _registration;
    private IToolPanelContent? _selected;
    private string _pageId = string.Empty;
    private string? _failure;
    private long _selectionVersion;

    internal ToolHostedPageGuard(Action<string> log) => _log = log;

    internal bool HasFailure => _failure is not null;
    internal string FailureMessage => HasFailure
        ? "此工具暂时无法显示。可以选择其他工具或关闭窗口。" : string.Empty;
    internal IToolPanelContent? Current => _session.Current;

    internal bool Select(object? registration, IToolPanelContent? content, string pageId)
    {
        if (ReferenceEquals(_registration, registration) && ReferenceEquals(_selected, content)) return false;
        long beforeHide = _selectionVersion;
        Hide();
        // OnHidden may navigate again. The most recent selection wins rather
        // than being overwritten when the departing callback returns.
        if (_selectionVersion != beforeHide) return true;
        _registration = registration;
        _selected = content;
        _pageId = pageId;
        _failure = null;
        _selectionVersion++;
        return true;
    }

    internal (float Height, float Width) Measure()
    {
        if (_selected is null || HasFailure) return (ToolDrawerLayout.MinimumHostedHeight, 0f);
        IToolPanelContent selected = _selected;
        object? registration = _registration;
        long selectionVersion = _selectionVersion;
        string pageId = _pageId;
        string phase = "height";
        try
        {
            float height = selected.PreferredHeight;
            if (!IsSelected(registration, selected, selectionVersion)) return (ToolDrawerLayout.MinimumHostedHeight, 0f);
            phase = "width";
            float width = selected is IToolPanelSizing sizing ? sizing.PreferredWidth : 0f;
            return IsSelected(registration, selected, selectionVersion) ? (height, width) : (ToolDrawerLayout.MinimumHostedHeight, 0f);
        }
        catch (Exception ex)
        {
            Fail(registration, selected, selectionVersion, pageId, phase, ex);
            return (ToolDrawerLayout.MinimumHostedHeight, 0f);
        }
    }

    internal void Show()
    {
        if (_selected is null || HasFailure || _session.Advance(_selected) is not { } change) return;
        IToolPanelContent selected = _selected;
        object? registration = _registration;
        long selectionVersion = _selectionVersion;
        string pageId = _pageId;
        // Select has already hidden the previous registration; Current is set
        // before OnShown so even a partially initialized provider gets one hide.
        InvokeHidden(change.Hidden);
        if (change.Shown is not IToolPanelLifecycle lifecycle) return;
        try { lifecycle.OnShown(); }
        catch (Exception ex) { Fail(registration, selected, selectionVersion, pageId, "shown", ex); }
    }

    internal bool Draw(IToolPanelSurface surface)
    {
        if (HasFailure || _selected is null || !ReferenceEquals(_session.Current, _selected)) return false;
        IToolPanelContent selected = _selected;
        object? registration = _registration;
        long selectionVersion = _selectionVersion;
        string pageId = _pageId;
        try
        {
            selected.Draw(surface);
            return IsSelected(registration, selected, selectionVersion) && ReferenceEquals(_session.Current, selected);
        }
        catch (Exception ex) { Fail(registration, selected, selectionVersion, pageId, "draw", ex); return false; }
    }

    internal ToolPanelBuilder DrawFrame(ToolPanelBuilder offered) => Draw(offered)
        ? offered : new ToolPanelBuilder(offered.Content, ToolPanelPointer.None, null, offered.BoundsInPixels);

    internal void Hide()
    {
        if (_session.Reset() is { } change) InvokeHidden(change.Hidden);
    }

    private bool IsSelected(object? registration, IToolPanelContent content, long selectionVersion) =>
        _selectionVersion == selectionVersion && ReferenceEquals(_registration, registration) && ReferenceEquals(_selected, content);

    private void Fail(object? registration, IToolPanelContent content, long selectionVersion, string pageId, string phase, Exception cause)
    {
        // A provider may select another tool from inside a callback. Its old
        // failure must not quarantine the newly selected registration.
        if (!IsSelected(registration, content, selectionVersion)) { Log(pageId, phase, cause); return; }
        if (!HasFailure)
        {
            _failure = phase;
            Log(pageId, phase, cause);
        }
        Hide();
    }

    private void InvokeHidden(IToolPanelContent? content)
    {
        if (content is not IToolPanelLifecycle lifecycle) return;
        string pageId = _pageId;
        try { lifecycle.OnHidden(); }
        catch (Exception ex)
        {
            // Reset removed Current before this call, so repeated hides cannot
            // invoke or log it again. Do not let reentrant navigation transfer
            // the departing page's diagnostic state to its successor.
            Log(pageId, "hidden", ex);
        }
    }

    private void Log(string pageId, string phase, Exception cause)
    {
        string detail;
        try { detail = cause.GetType().Name + ": " + cause.Message; }
        catch { detail = "Provider exception details were unavailable."; }
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        if (detail.Length > 600) detail = detail[..600] + "…";
        // A diagnostic sink is not a reason to take down otherwise healthy UI.
        pageId = pageId.Replace('\r', ' ').Replace('\n', ' ');
        if (pageId.Length > 160) pageId = pageId[..160];
        try { _log($"Hosted tool page '{pageId}' {phase} callback failed: {detail}"); }
        catch { }
    }
}
