extern alias unitycore;
extern alias unitytext;
extern alias unityui;
extern alias unityuimodule;

using Rukari.Lib.Settings;
using Rukari.Lib.Tools;
using Rukari.Lib.Runtime.Tools;
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
using RectMask2D = unityui::UnityEngine.UI.RectMask2D;
using RectTransform = unitycore::UnityEngine.RectTransform;
using RenderMode = unityuimodule::UnityEngine.RenderMode;
using Screen = unitycore::UnityEngine.Screen;
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;
using Transform = unitycore::UnityEngine.Transform;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector3 = unitycore::UnityEngine.Vector3;
using VerticalWrapMode = unitytext::UnityEngine.VerticalWrapMode;

namespace Rukari.Lib.Runtime.Settings;

/// <summary>Passive uGUI primitives; the existing shared NGUI guard remains the only native input owner.</summary>
internal sealed class SettingsWindow : IDisposable
{
    private const string InputOwner = "rukari.mod-settings";
    private const float Width = 1120f, Height = 720f, Header = 66f;
    private const float ViewX = 292f, ViewY = 56f, ViewWidth = 792f, ViewHeight = 540f;
    private const int VisiblePages = 10, MaximumElements = 512;
    private readonly ToolInputService _input;
    private readonly Action<string> _log;
    private readonly IToolInputRegion _region;
    private readonly GameObject _canvas;
    private readonly CanvasScaler _scaler;
    private readonly Font _font;
    private readonly Control _dimmer, _panel, _close, _previous, _next, _scrollTrack, _scrollThumb;
    private readonly Text _title, _pageTitle, _footer;
    private readonly OfficialPopupFrame _frame;
    private readonly RectTransform _viewport, _content;
    private readonly Control[] _pages = new Control[VisiblePages];
    private readonly List<Control> _elements = new();
    private ToolPanelElement[] _lastElements = Array.Empty<ToolPanelElement>();
    private ModSettingsPage? _selectedPage;
    private string? _sizeError;
    private float _scale, _x, _y, _scroll, _contentHeight;
    private float _pointerX, _pointerY;
    private bool _pointerValid, _shown, _disposed, _imeOn;
    private string? _pressed, _chromePressed, _focused;
    private int _firstPage, _releaseFrames;

    internal SettingsWindow(ToolInputService input, Action<string> log)
    {
        _input = input;
        _log = log;
        var region = input.RegisterRegion(InputOwner, "global-settings");
        if (!region.Success) throw new InvalidOperationException(region.Error!.Message);
        _region = region.Value;
        GameObject? root = null;
        try
        {
            _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei UI", 22) ?? Font.GetDefault();
            root = new GameObject("Rukari global mod settings");
            root.SetActive(false);
            _canvas = root;
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32730;
            canvas.pixelPerfect = false;
            _scaler = root.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            Object.DontDestroyOnLoad(root);
            _dimmer = CreateControl("ModalShade", root.transform);
            _dimmer.Image.sprite = null;
            _dimmer.Image.color = new Color(0f, 0f, 0f, .28f);
            _panel = CreateControl("ModSettingsWindow", root.transform);
            _frame = new OfficialPopupFrame(_panel.Rect, _panel.Image);
            _title = CreateText("Title", _panel.Rect, "模组设置", 30, TextAnchor.MiddleCenter);
            _title.fontStyle = FontStyle.Bold;
            _title.color = OfficialPopupFrame.Heading;
            _close = CreateControl("Close", _panel.Rect);
            for (int i = 0; i < _pages.Length; i++) _pages[i] = CreateControl("Module" + i, _panel.Rect);
            _previous = CreateControl("PreviousModules", _panel.Rect);
            _next = CreateControl("NextModules", _panel.Rect);
            _pageTitle = CreateText("ModuleTitle", _panel.Rect, "", 24, TextAnchor.MiddleLeft);
            _pageTitle.fontStyle = FontStyle.Bold;
            _footer = CreateText("Footer", _panel.Rect, "", 16, TextAnchor.MiddleLeft);
            var divider = CreateControl("ModuleDivider", _panel.Rect);
            SetRect(divider.Rect, 273f, ViewY, 2f, ViewHeight + 6f);
            divider.Image.color = new Color(.77f, .82f, .87f, 1f);
            divider.GameObject.SetActive(true);
            var viewportObject = new GameObject("SettingsViewport");
            _viewport = viewportObject.AddComponent<RectTransform>();
            _viewport.SetParent(_panel.Rect, false);
            viewportObject.AddComponent<RectMask2D>();
            var contentObject = new GameObject("SettingsContent");
            _content = contentObject.AddComponent<RectTransform>();
            _content.SetParent(_viewport, false);
            _scrollTrack = CreateControl("ContentScrollTrack", _panel.Rect);
            _scrollThumb = CreateControl("ContentScrollThumb", _panel.Rect);
        }
        catch
        {
            if (root is not null) Object.Destroy(root);
            _region.Dispose();
            throw;
        }
    }

    internal void Show()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SettingsWindow));
        if (_shown) return;
        _shown = true;
        _releaseFrames = 0;
        _pressed = _chromePressed = null;
        _focused = null;
        _pointerValid = false;
        // Publish before displaying any controls; the click that opened this window must not reach its body.
        _region.Update(Array.Empty<ToolInputRect>(), captureAllPointer: true);
        _input.SetKeyboardCapture(InputOwner, true);
        _canvas.SetActive(true);
    }

    internal void Hide()
    {
        if (!_shown || _disposed) return;
        _shown = false;
        _canvas.SetActive(false);
        _releaseFrames = 2;
        _selectedPage = null;
        _sizeError = null;
        _pressed = _chromePressed = _focused = null;
        _lastElements = Array.Empty<ToolPanelElement>();
        _pointerValid = false;
        SetIme(false);
        _input.SetKeyboardCapture(InputOwner, false);
        // Keep the closing click captured until the next complete pump.
        _region.Update(Array.Empty<ToolInputRect>(), captureAllPointer: true);
    }

    internal void Tick(ModSettingsService service)
    {
        if (_disposed) return;
        if (!_shown || !service.IsOpen)
        {
            if (_shown) Hide();
            if (_releaseFrames > 0 && --_releaseFrames == 0)
                _region.Update(Array.Empty<ToolInputRect>());
            return;
        }
        _input.SetKeyboardCapture(InputOwner, true);
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { service.Close(); return; }

        int pixelWidth = Screen.width, pixelHeight = Screen.height;
        if (pixelWidth < 1 || pixelHeight < 1) { service.Close(); return; }
        _scale = Math.Max(.01f, Math.Min(1.25f, Math.Min((pixelWidth - 32f) / Width, (pixelHeight - 40f) / Height)));
        _scaler.scaleFactor = _scale;
        _x = (pixelWidth / _scale - Width) / 2f;
        _y = (pixelHeight / _scale - Height) / 2f;
        SetRect(_dimmer.Rect, 0f, 0f, pixelWidth / _scale, pixelHeight / _scale);
        _dimmer.GameObject.SetActive(true);
        SetRect(_panel.Rect, _x, _y, Width, Height);
        _panel.GameObject.SetActive(true);
        SetRect(_title.GetComponent<RectTransform>(), 84f, Height - Header, Width - 168f, Header);
        OfficialPopupFrame.CenterTitle(_title, Height - Header, Header);
        _frame.Layout(Width, Height, Header, new ToolInputRect(84f, Height - Header, Width - 168f, Header), _title.preferredWidth);
        SetRect(_pageTitle.GetComponent<RectTransform>(), ViewX, Height - Header - 48f, ViewWidth, 38f);
        SetRect(_footer.GetComponent<RectTransform>(), ViewX, 16f, ViewWidth, 28f);
        SetRect(_viewport, ViewX, ViewY, ViewWidth, ViewHeight);
        var raw = UnityEngine.Input.mousePosition;
        float localX = raw.x / _scale - _x, localY = raw.y / _scale - _y;
        bool pressed = UnityEngine.Input.GetMouseButtonDown(0), released = UnityEngine.Input.GetMouseButtonUp(0);
        bool down = UnityEngine.Input.GetMouseButton(0);
        if (pressed) _pressed = _chromePressed = null;

        bool close = DrawButton(_close, "close", "×", new ToolInputRect(Width - 64f, Height - 58f, 48f, 48f),
            true, false, localX, localY, pressed, released, down);
        var closeSprite = AtlasEmblemSource.GetSprite("Common:Common_Icon_Close", sliced: false);
        _close.Image.enabled = closeSprite is not null;
        if (closeSprite is not null)
        {
            _close.Image.sprite = closeSprite;
            _close.Image.type = Image.Type.Simple;
            _close.Image.preserveAspect = true;
            _close.Image.color = OfficialPopupFrame.Heading;
            _close.Label.text = "";
        }
        _close.Label.fontSize = 42;
        _close.Label.color = OfficialPopupFrame.Heading;
        if (close) { service.Close(); return; }

        var pages = service.Pages;
        int maxFirst = Math.Max(0, pages.Count - VisiblePages);
        float wheel = UnityEngine.Input.mouseScrollDelta.y;
        if (localX >= 24f && localX <= 260f && localY >= ViewY && localY <= ViewY + ViewHeight && wheel != 0)
            _firstPage -= (int)MathF.Sign(wheel);
        _firstPage = Math.Clamp(_firstPage, 0, maxFirst);
        for (int i = 0; i < _pages.Length; i++)
        {
            int at = _firstPage + i;
            if (at >= pages.Count) { _pages[i].GameObject.SetActive(false); continue; }
            var page = pages[at];
            if (DrawButton(_pages[i], "page:" + page.OwnerId, page.Title,
                new ToolInputRect(24f, ViewY + ViewHeight - 48f - i * 50f, 230f, 43f),
                true, service.SelectedPage?.OwnerId == page.OwnerId, localX, localY, pressed, released, down))
            {
                var opened = service.Open(page.OwnerId);
                if (!opened.Success) _log($"[mod-settings] Cannot select '{page.OwnerId}': {opened.Error!.Message}");
                _pressed = _chromePressed = null;
                released = false;
            }
        }
        bool showNavigation = pages.Count > VisiblePages;
        _previous.GameObject.SetActive(showNavigation);
        _next.GameObject.SetActive(showNavigation);
        if (showNavigation)
        {
            if (DrawButton(_previous, "modules-up", "上一页", new ToolInputRect(24f, ViewY + 4f, 108f, 32f),
                _firstPage > 0, false, localX, localY, pressed, released, down)) _firstPage = Math.Max(0, _firstPage - VisiblePages);
            if (DrawButton(_next, "modules-down", "下一页", new ToolInputRect(146f, ViewY + 4f, 108f, 32f),
                _firstPage < maxFirst, false, localX, localY, pressed, released, down)) _firstPage = Math.Min(maxFirst, _firstPage + VisiblePages);
        }

        var selected = service.SelectedPage;
        _pageTitle.text = selected?.Title ?? "暂无设置";
        if (!ReferenceEquals(_selectedPage, selected))
        {
            // Owner IDs can be reused after a registration lease is disposed. Interaction state belongs to
            // this registration object, not its name; selecting it again also permits a failed size retry.
            _selectedPage = selected;
            _sizeError = null;
            _scroll = 0f;
            _lastElements = Array.Empty<ToolPanelElement>();
            _focused = _pressed = _chromePressed = null;
            _pointerValid = false;
            SetIme(false);
        }
        float preferredHeight = ViewHeight;
        string? sizeError = _sizeError;
        if (selected is not null)
        {
            try
            {
                if (sizeError is null) preferredHeight = selected.Content.PreferredHeight;
            }
            catch (Exception ex)
            {
                _sizeError = sizeError = "无法读取此模组的设置尺寸。";
                _log($"[mod-settings] '{selected.OwnerId}' size failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        _contentHeight = float.IsFinite(preferredHeight) ? Math.Clamp(preferredHeight, ViewHeight, 8000f) : ViewHeight;
        bool inView = new ToolInputRect(ViewX, ViewY, ViewWidth, ViewHeight).Contains(localX, localY);
        if (inView && wheel != 0 && !down) _scroll -= wheel * 54f;
        _scroll = Math.Clamp(_scroll, 0f, _contentHeight - ViewHeight);
        float contentY = ViewHeight - _contentHeight + _scroll;
        SetRect(_content, 0f, contentY, ViewWidth, _contentHeight);
        float pointerX = localX - ViewX, pointerY = localY - ViewY - contentY;
        if (pressed && inView) _pressed = ToolPanelBuilder.HitTest(_lastElements, pointerX, pointerY);
        var pointer = new ToolPanelPointer(pointerX, pointerY, down, pressed && inView, released && inView,
            _pointerValid ? pointerX - _pointerX : 0f, _pointerValid ? pointerY - _pointerY : 0f);
        _pointerX = pointerX; _pointerY = pointerY; _pointerValid = true;
        var builder = new ToolPanelBuilder(new ToolInputRect(0f, 0f, ViewWidth, _contentHeight), pointer, _pressed,
            new ToolInputRect((_x + ViewX) * _scale, (_y + ViewY + contentY) * _scale, ViewWidth * _scale, _contentHeight * _scale),
            ReadKeyboard());
        if (sizeError is null && selected is not null) service.DrawSelected(builder);
        string? pageError = sizeError ?? service.LastPageError;
        if (!string.IsNullOrEmpty(pageError))
        {
            builder = new ToolPanelBuilder(new ToolInputRect(0f, 0f, ViewWidth, _contentHeight), ToolPanelPointer.None);
            builder.Text("error", "此模组的设置暂时无法显示。可以选择其他模组或关闭窗口。",
                new ToolInputRect(0f, _contentHeight - 86f, ViewWidth, 80f), 22);
            builder.Status("error-detail", Limit(pageError, 1000), new ToolInputRect(0f, _contentHeight - 246f, ViewWidth, 150f));
        }
        _focused = builder.FocusedField;
        SetIme(_focused is not null);
        _lastElements = builder.Elements.Take(MaximumElements).ToArray();
        DrawElements(_lastElements, pointer);
        _footer.text = builder.Elements.Count > MaximumElements ? "此模组的设置控件过多，已限制显示。"
            : _contentHeight > ViewHeight ? "滚动查看全部设置。" : "";
        LayoutContentScrollbar();
        if (released) _pressed = _chromePressed = null;
    }

    private void LayoutContentScrollbar()
    {
        bool scrollable = _contentHeight > ViewHeight;
        _scrollTrack.GameObject.SetActive(scrollable);
        _scrollThumb.GameObject.SetActive(scrollable);
        if (!scrollable) return;
        SetRect(_scrollTrack.Rect, ViewX + ViewWidth + 9f, ViewY, 5f, ViewHeight);
        ToolTheme.Apply(_scrollTrack.Image, "scroll.track");
        float thumb = Math.Max(28f, ViewHeight * ViewHeight / _contentHeight);
        float y = ViewY + (ViewHeight - thumb) * (1f - _scroll / (_contentHeight - ViewHeight));
        SetRect(_scrollThumb.Rect, ViewX + ViewWidth + 7f, y, 9f, thumb);
        ToolTheme.Apply(_scrollThumb.Image, "scroll.thumb");
    }

    private bool DrawButton(Control control, string id, string label, ToolInputRect bounds, bool enabled,
        bool selected, float x, float y, bool pressed, bool released, bool down)
    {
        bool hover = bounds.Contains(x, y);
        if (pressed && hover && enabled) _chromePressed = id;
        control.GameObject.SetActive(true);
        SetRect(control.Rect, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        control.Image.enabled = true;
        control.Label.text = ToolDrawerLayout.FitSingleLine(Limit(label, 200), 20, bounds.Width - 22f);
        control.Label.fontSize = 20;
        control.Label.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
        control.Label.color = OfficialButtonSkin.Apply(control.Image, bounds.Width, bounds.Height,
            enabled, selected, hover, down && _chromePressed == id, false, false, true);
        ToolTheme.CenterButtonLabel(control.Label, bounds.Width, bounds.Height, 8f);
        return enabled && hover && released && _chromePressed == id;
    }

    private void DrawElements(IReadOnlyList<ToolPanelElement> elements, ToolPanelPointer pointer)
    {
        int used = 0;
        foreach (var element in elements)
        {
            if (element.Kind == ToolPanelElementKind.Area || !element.Bounds.IsValid) continue;
            if (_elements.Count <= used) _elements.Add(CreateControl("SettingsElement" + used, _content));
            var control = _elements[used++];
            var r = element.Bounds;
            control.GameObject.SetActive(true);
            SetRect(control.Rect, r.X, r.Y, r.Width, r.Height);
            control.Label.fontSize = Math.Clamp(element.Size, 1, 80);
            control.Label.fontStyle = FontStyle.Normal;
            control.Label.text = Limit(element.Text, 4000);
            control.Label.horizontalOverflow = HorizontalWrapMode.Wrap;
            control.Label.alignment = TextAnchor.MiddleCenter;
            SetRect(control.Label.GetComponent<RectTransform>(), 4f, 0f, Math.Max(1f, r.Width - 8f), r.Height);
            control.Label.color = ToolTheme.Foreground("button");
            control.Image.enabled = true;
            switch (element.Kind)
            {
                case ToolPanelElementKind.Text:
                case ToolPanelElementKind.Status:
                    control.Image.enabled = false;
                    control.Label.alignment = TextAnchor.UpperLeft;
                    control.Label.color = ToolTheme.Foreground(element.Kind == ToolPanelElementKind.Status ? "button.disabled" : "button");
                    break;
                case ToolPanelElementKind.Separator:
                    SetRect(control.Rect, r.X, r.Y + r.Height - 2f, r.Width, 2f);
                    control.Label.text = "";
                    ToolTheme.Apply(control.Image, "header");
                    break;
                case ToolPanelElementKind.Swatch:
                    control.Label.text = "";
                    control.Image.sprite = null;
                    control.Image.type = Image.Type.Simple;
                    control.Image.color = ToolTheme.ParseColour(element.Colour, Color.white);
                    break;
                case ToolPanelElementKind.Button:
                    bool hover = r.Contains(pointer.X, pointer.Y);
                    bool primary = element.Style == ToolSurfaceStyle.Primary;
                    control.Label.text = ToolDrawerLayout.FitSingleLine(Limit(element.Text, 200), control.Label.fontSize, r.Width - 16f);
                    control.Label.color = OfficialButtonSkin.Apply(control.Image, r.Width, r.Height,
                        element.Enabled, element.Highlighted || element.Style == ToolSurfaceStyle.Selected,
                        hover, pointer.Down && _pressed == element.Id, primary || element.Style == ToolSurfaceStyle.Undo, primary, false);
                    ToolTheme.CenterButtonLabel(control.Label, r.Width, r.Height, 6f);
                    break;
                case ToolPanelElementKind.Field:
                    ToolTheme.Apply(control.Image, element.Highlighted ? "button.selected" : "solid");
                    control.Label.alignment = TextAnchor.MiddleLeft;
                    if (element.Highlighted)
                    {
                        try { UnityEngine.Input.compositionCursorPos = new Vector2((_x + ViewX + r.X) * _scale,
                            (_y + ViewY + ViewHeight - _contentHeight + _scroll + r.Y + r.Height) * _scale); }
                        catch { /* Composition positioning is optional. */ }
                    }
                    break;
                default:
                    ToolTheme.Apply(control.Image, ToolSurfaceStyles.ThemeKey(element.Style));
                    break;
            }
        }
        for (int i = used; i < _elements.Count; i++) _elements[i].GameObject.SetActive(false);
    }

    private ToolPanelKeyboard ReadKeyboard()
    {
        if (_focused is null) return ToolPanelKeyboard.None;
        string typed = UnityEngine.Input.inputString ?? "";
        bool control = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
        if (control && UnityEngine.Input.GetKeyDown(KeyCode.V))
        {
            try { typed = UnityEngine.GUIUtility.systemCopyBuffer ?? ""; }
            catch { typed = ""; }
        }
        typed = Limit(new string(typed.Where(c => !char.IsControl(c)).ToArray()), 1024);
        string composition = "";
        try { composition = UnityEngine.Input.compositionString ?? ""; }
        catch { /* Platforms without IME still support committed input. */ }
        return new ToolPanelKeyboard(typed, UnityEngine.Input.GetKeyDown(KeyCode.Backspace),
            UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter), false,
            UnityEngine.Input.GetKeyDown(KeyCode.Delete), UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow),
            UnityEngine.Input.GetKeyDown(KeyCode.RightArrow), UnityEngine.Input.GetKeyDown(KeyCode.Home),
            UnityEngine.Input.GetKeyDown(KeyCode.End), control && UnityEngine.Input.GetKeyDown(KeyCode.A), composition);
    }

    private void SetIme(bool on)
    {
        if (_imeOn == on) return;
        try { UnityEngine.Input.imeCompositionMode = on ? UnityEngine.IMECompositionMode.On : UnityEngine.IMECompositionMode.Auto; _imeOn = on; }
        catch { /* No extra native input hooks are installed for IME. */ }
    }

    private Control CreateControl(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.color = Color.white;
        return new Control(go, rect, image, CreateText(name + "Text", rect, "", 20, TextAnchor.MiddleCenter));
    }

    private Text CreateText(string name, Transform parent, string text, int size, TextAnchor alignment)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var label = go.AddComponent<Text>();
        label.font = _font;
        label.text = text;
        label.fontSize = size;
        label.color = OfficialPopupFrame.Heading;
        label.alignment = alignment;
        label.supportRichText = false;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        return label;
    }

    private static void SetRect(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
        rect.localScale = Vector3.one;
    }

    private static string Limit(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shown = false;
        // Release managed ownership first: native teardown may meet an already destroyed scene object.
        _input.SetKeyboardCapture(InputOwner, false);
        _region.Dispose();
        SetIme(false);
        try { Object.Destroy(_canvas); }
        catch (Exception ex) { _log($"[mod-settings] Window native cleanup failed after releasing input: {ex.Message}"); }
    }

    private sealed record Control(GameObject GameObject, RectTransform Rect, Image Image, Text Label);
}
