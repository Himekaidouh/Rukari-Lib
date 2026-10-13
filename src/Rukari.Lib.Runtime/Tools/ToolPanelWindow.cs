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
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;
using Time = unitycore::UnityEngine.Time;
using Transform = unitycore::UnityEngine.Transform;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector3 = unitycore::UnityEngine.Vector3;
using VerticalWrapMode = unitytext::UnityEngine.VerticalWrapMode;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// One standalone tool window: the shared frame, theme and input ownership drawn around a page that fills its own
/// content rectangle, on a surface the user can move, resize and keep beside the drawer.
///
/// <para>
/// It is deliberately NOT a second <see cref="ToolboxDrawer"/>. The drawer is the rail — one permanent icon, one
/// module column, one modal sheet — and its whole shape exists because there is only one of it. A window is the
/// opposite: there can be several, each is placed and sized on its own, and none of them hides the others. What
/// the two share is the contract: a page fills a content rectangle through <see cref="IToolPanelSurface"/> and
/// never creates a canvas of its own.
/// </para>
///
/// <para>
/// The window is drawn at a scale of one, so a canvas unit is a physical pixel. The user resizes this surface
/// directly, and a scale that followed the screen would make the contents move by a different distance than the
/// pointer travelled. It is also the reason the window never asks a page for a scale: a page reads
/// <see cref="IToolPanelSurface.BoundsInPixels"/> when it needs screen pixels.
/// </para>
///
/// <para>
/// A press belongs to exactly one thing, in this order: the title band (drag), the resize grip (resize), the close
/// button, then the page's own controls. Chrome is resolved before the page because chrome is drawn over it; a
/// press that lands on chrome never reaches the page underneath, which is the same rule the drawer applies to its
/// own title band.
/// </para>
/// </summary>
internal sealed class ToolPanelWindow : IDisposable
{
    private const int TitleFontSize = 26;
    private const int CloseGlyphFontSize = 34;
    private const int DefaultPoolSize = 16;
    /// <summary>
    /// Draw order of the bottom window. It sits between the drawer's sheet (32710) and the global settings window
    /// (32730), and each window above the bottom one is raised by one, which is what makes press-to-front visible.
    /// </summary>
    private const int BaseSortingOrder = 32720;

    private readonly ToolInputService _input;
    private readonly ToolWindowStack _stack;
    private readonly IToolPanelContent _content;
    private readonly ToolWindowPlacementStore _placements;
    private readonly ToolHostedPageGuard _guard;
    private readonly Action<string> _log;
    private readonly string _windowId;
    private readonly string _title;
    private readonly string _keyboardOwner;
    private readonly IToolInputRegion _region;
    private readonly Font _font;

    private readonly GameObject _canvasObject;
    private readonly Canvas _canvas;
    private readonly CanvasScaler _scaler;
    private readonly Control _panel;
    private readonly OfficialPopupFrame _frame;
    private readonly Text _titleText;
    private readonly Control _close;
    private readonly Image _closeGlyph;
    /// <summary>Whether the atlas close glyph resolved; without it the plate and the × character are drawn instead.</summary>
    private readonly bool _closeArt;
    private readonly Control _grip;
    private readonly Color _darkText = ToolTheme.Native(ToolPalette.Text);

    private Control[] _hosted = Array.Empty<Control>();
    /// <summary>
    /// The previous frame's controls, kept by reference rather than copied: the builder that produced them is
    /// discarded at the end of the frame, so the list is never written to again, and a copy per frame was steady-state
    /// garbage in the one place that has to stay cheap.
    /// </summary>
    private IReadOnlyList<ToolPanelElement> _hostedLast = Array.Empty<ToolPanelElement>();
    private string? _hostedPressed;
    private string? _hostedField;
    private int _hostedDrawn;
    private string _hostedIds = "";
    private bool _overflowLogged;
    private bool _contentDragging;
    private float _pointerX;
    private float _pointerY;
    private bool _pointerValid;

    private string _typed = "";
    private bool _backspace, _submit, _cancel, _delete, _left, _right, _home, _end, _selectAll;
    private string _composition = "";
    private ToolInputRect _fieldRect;
    private bool _imeOn, _imeFailureLogged;

    /// <summary>
    /// The pointer rules — drag, resize, close, reset, and whether the size still follows the page — as a value the
    /// host feeds one frame at a time. Everything this class does with a pointer goes through it, so those rules are
    /// tested without a game instead of being verified by dragging a window in one.
    /// </summary>
    private readonly ToolWindowInteraction _interaction = new(default, resizable: false);
    /// <summary>The registration identity of the current showing; see <see cref="Show"/> for why it is renewed.</summary>
    private object? _showing;
    private bool _placementResolved;
    private string _loggedPlacement = "";
    private bool _shown, _disposed;
    private int _releaseKeyboardFrames;
    private int _logFrames;

    internal ToolPanelWindow(ToolInputService input, string windowId, string title,
        IToolPanelContent content, ToolWindowPlacementStore placements, ToolWindowStack stack, Action<string> log)
    {
        _input = input;
        _windowId = windowId;
        _title = title;
        _content = content;
        _placements = placements;
        _stack = stack;
        _log = log;
        _keyboardOwner = "tool-window:" + windowId;
        _guard = new ToolHostedPageGuard(log);
        var registration = input.RegisterRegion("rukari.lib.runtime", "tool-window:" + windowId);
        if (!registration.Success) throw new InvalidOperationException(registration.Error!.Message);
        _region = registration.Value;
        GameObject? root = null;
        try
        {
            _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei UI", 22) ?? Font.GetDefault();
            root = new GameObject("Rukari tool window " + windowId);
            root.SetActive(false);
            _canvasObject = root;
            var canvas = root.AddComponent<Canvas>();
            _canvas = canvas;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Between the drawer (32710) and the global settings window (32730). Windows raise themselves above
            // each other by one step whenever one of them is pressed.
            canvas.sortingOrder = BaseSortingOrder;
            canvas.pixelPerfect = false;
            _scaler = root.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            Object.DontDestroyOnLoad(root);

            var panel = CreateImage("WindowPanel", root.transform, "panel");
            _panel = new Control(panel.GameObject, panel.Rect, panel.Image,
                CreateText("WindowTitle", panel.Rect, title, TitleFontSize, TextAnchor.MiddleCenter));
            _frame = new OfficialPopupFrame(panel.Rect, panel.Image);
            _titleText = _panel.Label;
            _titleText.fontStyle = FontStyle.Bold;
            _titleText.color = OfficialPopupFrame.Heading;
            _titleText.horizontalOverflow = HorizontalWrapMode.Overflow;

            _close = CreateControl("WindowClose", panel.Rect);
            var glyph = CreateImage("WindowCloseGlyph", _close.Rect, "solid");
            _closeGlyph = glyph.Image;
            _closeGlyph.enabled = false;
            var closeSprite = AtlasEmblemSource.GetSprite("Common:Common_Icon_Close", sliced: false);
            if (closeSprite is not null)
            {
                _closeArt = true;
                _closeGlyph.sprite = closeSprite;
                _closeGlyph.type = Image.Type.Simple;
                _closeGlyph.preserveAspect = true;
            }
            _grip = CreateControl("WindowGrip", panel.Rect);
            _grip.Label.text = "";
            ToolTheme.Apply(_grip.Image, "header");
        }
        catch
        {
            if (root is not null) Object.Destroy(root);
            _region.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the user wants this window on screen. It is the window's own state rather than the host's because
    /// the close button changes it too; the host only reads it. Leaving the workspace does NOT clear it, so a
    /// window the user opened comes back when the editor does.
    /// </summary>
    internal bool Requested { get; set; }

    internal bool IsVisible => _shown && !_disposed;

    /// <summary>
    /// One frame. <paramref name="allowed"/> is false while the tool workspace is not on screen — the window
    /// releases its input and hides its canvas, and comes back unchanged when the editor does.
    /// </summary>
    internal void Tick(bool allowed)
    {
        if (_disposed) return;
        if (!Requested || !allowed)
        {
            if (_shown) Hide();
            else if (_releaseKeyboardFrames > 0 && --_releaseKeyboardFrames == 0) _region.Update(Array.Empty<ToolInputRect>());
            return;
        }
        int pixelWidth = Screen.width, pixelHeight = Screen.height;
        if (pixelWidth <= 0 || pixelHeight <= 0) { Hide(); return; }
        if (!_shown) Show();
        _canvasObject.SetActive(true);
        _canvas.scaleFactor = 1f;
        _scaler.scaleFactor = 1f;
        if (!_placementResolved)
        {
            _interaction.Adopt(ResolvePlacement(pixelWidth, pixelHeight));
            _placementResolved = true;
        }
        else
        {
            // Every frame, so a window placed on a larger display is pulled back into view after a resize rather
            // than drawn where it can no longer be grabbed.
            _interaction.KeepOnScreen(pixelWidth, pixelHeight);
        }
        FollowPageSize(pixelWidth, pixelHeight);
        // Publish the real rectangle for hit testing and take this window's place in the order. A window that is
        // already on screen keeps its place: only a press or a fresh appearance moves it.
        _stack.Show(_windowId, _interaction.Placement);
        // One draw order per window, and the table that decides which window owns a press must agree with it. The
        // range is exactly as wide as the number of windows that may be registered (ToolsHost.MaximumWindows), and
        // it stops one short of the global settings window at 32730.
        int order = BaseSortingOrder + Math.Clamp(_stack.Depth(_windowId), 0, 9);
        if (_canvas.sortingOrder != order) _canvas.sortingOrder = order;
        var raw = UnityEngine.Input.mousePosition;
        HandleKeyboard();
        HandleChrome(raw, pixelWidth, pixelHeight);
        if (!Requested) { Hide(); return; }
        DrawChrome(raw);
        DrawContent(raw);
        Publish();
        LogFrame();
    }

    /// <summary>
    /// Writes the placement and control-set diagnostics: at most once per change, never mid-gesture, and only a few
    /// times a second. Formatting two strings on every frame would be steady-state garbage for information that can
    /// only change when the user does something.
    /// </summary>
    private void LogFrame()
    {
        if (_interaction.IsBusy) return;
        if (++_logFrames % 15 != 0) return;
        LogPlacementOnce();
        LogElementsOnce(_hostedLast, _hostedDrawn);
    }

    private ToolWindowPlacement ResolvePlacement(int pixelWidth, int pixelHeight)
    {
        (float preferredHeight, float preferredWidth) = _guard.Measure();
        if (_placements.TryGet(_windowId, pixelWidth, pixelHeight, out ToolWindowPlacement stored)) return stored;
        return ToolWindowLayout.DefaultPlacement(pixelWidth, pixelHeight, preferredWidth, preferredHeight);
    }

    /// <summary>
    /// Follows the size the page asks for, which is how a page with two views — a chooser and a detail sheet —
    /// gets the second view's height instead of drawing outside the window it was given. It stops following as
    /// soon as the user resizes: after that the size on screen is the one they chose.
    /// </summary>
    private void FollowPageSize(int pixelWidth, int pixelHeight)
    {
        (float preferredHeight, float preferredWidth) = _guard.Measure();
        _interaction.FollowContentSize(preferredWidth, preferredHeight, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// The title band, the close button and the resize grip. Chrome is resolved before the page, so a press that
    /// lands on the frame is never also delivered to a control the page drew underneath it.
    /// </summary>
    private void HandleChrome(Vector2 raw, int pixelWidth, int pixelHeight)
    {
        var pointer = new ToolWindowPointer(raw.x, raw.y,
            UnityEngine.Input.GetMouseButtonDown(0), UnityEngine.Input.GetMouseButton(0));
        // A press that lands inside a window drawn above this one belongs to that window. The shared input guard is
        // a union of rectangles and cannot say so, so every host asks the table before acting on a press. A gesture
        // already in progress keeps the pointer: it captured every position when it started.
        bool owned = !pointer.Pressed || _stack.OwnerAt(raw.x, raw.y) == _windowId;
        if (pointer.Pressed && owned) _stack.Raise(_windowId);
        ToolWindowInteractionResult result = _interaction.Handle(pointer, owned, Time.unscaledTime, pixelWidth, pixelHeight);
        if (result.CloseRequested)
        {
            Requested = false;
            return;
        }
        if (result.ResetRequested)
        {
            if (_placements.Reset(_windowId)) _log?.Invoke($"[tool-window] {_windowId} placement reset to default");
            _interaction.Adopt(ResolvePlacement(pixelWidth, pixelHeight));
            _loggedPlacement = "";
            return;
        }
        // Stored once per gesture, when it ends, rather than on every frame it moves: the log line is meant to say
        // where the window ended up, not to narrate the drag.
        if (result.PlacementChanged && result.Ended != ToolWindowGesture.None)
        {
            Remember(result.Ended == ToolWindowGesture.Resize ? "resized" : "moved");
        }
    }

    private void DrawChrome(Vector2 raw)
    {
        ToolWindowPlacement placement = _interaction.Placement;
        SetRect(_panel.Rect, placement.X, placement.Y, placement.Width, placement.Height);
        ToolInputRect title = ToolWindowLayout.TitleBounds(placement.Width, placement.Height);
        ToolInputRect close = ToolWindowLayout.CloseBounds(placement.Width, placement.Height);
        float headerHeight = ToolWindowLayout.Padding + ToolWindowLayout.HeaderRowHeight - 4f;
        _titleText.text = ToolDrawerLayout.FitSingleLine(_title, TitleFontSize, title.Width);
        _titleText.fontSize = TitleFontSize;
        SetRect(_titleText.GetComponent<RectTransform>(), title.X, title.Y, title.Width, title.Height);
        OfficialPopupFrame.CenterTitle(_titleText, placement.Height - headerHeight, headerHeight);
        _frame.Layout(placement.Width, placement.Height, headerHeight, title, _titleText.preferredWidth);

        float localX = raw.x - placement.X, localY = raw.y - placement.Y;
        bool closeHover = close.Contains(localX, localY) && !_interaction.IsBusy;
        SetRect(_close.Rect, close.X, close.Y, close.Width, close.Height);
        SetRect(_close.Label.GetComponent<RectTransform>(), 0, 0, close.Width, close.Height);
        _close.Label.text = _closeArt ? "" : "×";
        _close.Label.fontSize = CloseGlyphFontSize;
        _close.Label.color = OfficialPopupFrame.Heading;
        _close.Image.enabled = !_closeArt;
        if (_closeArt)
        {
            SetRect(_closeGlyph.GetComponent<RectTransform>(), 0, 0, close.Width, close.Height);
            _closeGlyph.enabled = true;
            _closeGlyph.color = closeHover ? OfficialPopupFrame.Heading : ToolTheme.Foreground("button");
        }
        else
        {
            string closeSkin = closeHover ? "button.selected" : "button";
            if (!ToolTheme.Apply(_close.Image, closeSkin))
                _close.Image.color = ToolTheme.Native(ToolPalette.ForKey(closeSkin).Background);
        }

        // No resize grip is drawn: the window's size follows its page, and dragging the frame is what the user asked
        // for. The grip control stays hidden rather than destroyed so the rule can be offered again by flipping the
        // interaction, whose resize behaviour is implemented and tested either way.
        _grip.GameObject.SetActive(false);
    }

    private void DrawContent(Vector2 raw)
    {
        ToolWindowPlacement placement = _interaction.Placement;
        _contentDragging = false;
        // The page sees its OWN coordinate space, which starts at zero, and the conversion lives in the shared pure
        // helper so the host and the tests cannot disagree about it.
        ToolInputRect padded = ToolWindowLayout.ContentBounds(placement.Width, placement.Height);
        var content = new ToolInputRect(0f, 0f, padded.Width, padded.Height);
        (float pointerX, float pointerY) = ToolWindowFrame.ContentPointer(placement, raw.x, raw.y);
        float deltaX = _pointerValid ? pointerX - _pointerX : 0f;
        float deltaY = _pointerValid ? pointerY - _pointerY : 0f;
        _pointerX = pointerX;
        _pointerY = pointerY;
        _pointerValid = true;
        bool pressed = UnityEngine.Input.GetMouseButtonDown(0);
        bool released = UnityEngine.Input.GetMouseButtonUp(0);
        // A press is resolved against the PREVIOUS frame's layout, because it arrives before this frame's page has
        // drawn the control it landed on; a page's layout is stable from one frame to the next. A press owned by a
        // window drawn above this one resolves to nothing here — otherwise one click would drive two windows.
        if (pressed)
            _hostedPressed = _interaction.ChromeHolds(raw.x, raw.y) || _stack.OwnerAt(raw.x, raw.y) != _windowId
                ? null
                : ToolPanelBuilder.HitTest(_hostedLast, pointerX, pointerY);
        var pointer = new ToolPanelPointer(pointerX, pointerY, UnityEngine.Input.GetMouseButton(0),
            pressed, released, deltaX, deltaY);
        var keyboard = new ToolPanelKeyboard(_typed, _backspace, _submit, _cancel, _delete,
            _left, _right, _home, _end, _selectAll, _composition);
        var builder = new ToolPanelBuilder(content, pointer, _hostedPressed,
            ToolWindowLayout.ContentPixels(placement), keyboard);
        string failure;
        ToolPanelBuilder complete = _guard.DrawFrame(builder);
        if (ReferenceEquals(complete, builder))
        {
            _hostedField = builder.FocusedField;
            failure = string.Empty;
        }
        else
        {
            // Discard the failed page's partial frame, presses and typing; the window's own chrome stays usable.
            failure = _guard.FailureMessage;
            ResetInteraction();
            HideHosted();
            builder = complete;
        }
        IReadOnlyList<ToolPanelElement> elements = builder.Elements;
        _hostedLast = elements;
        RenderElements(elements, failure);
        _contentDragging = builder.IsDragging;
        if (released) _hostedPressed = null;
    }

    private void RenderElements(IReadOnlyList<ToolPanelElement> elements, string failure)
    {
        EnsurePool(elements.Count + (failure.Length != 0 ? 1 : 0));        EnsurePool(elements.Count + (failure.Length != 0 ? 1 : 0));
        int used = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            if (used >= _hosted.Length)
            {
                if (!_overflowLogged)
                {
                    _overflowLogged = true;
                    _log?.Invoke($"[tool-window] {_windowId} asked for more than {_hosted.Length} controls; the rest were not drawn.");
                }
                break;
            }
            ToolPanelElement element = elements[i];
            // An area draws nothing by itself: it is a rectangle the page hit-tests.
            if (element.Kind == ToolPanelElementKind.Area) continue;
            used = DrawElement(_hosted[used], element, used);
        }
        if (failure.Length != 0 && used < _hosted.Length)
        {
            float width = Math.Max(1f, _interaction.Placement.Width - ToolWindowLayout.Padding * 2);
            used = DrawElement(_hosted[used], new ToolPanelElement("window-error", ToolPanelElementKind.Status,
                new ToolInputRect(0f, 0f, width, ToolDrawerLayout.LineHeight(16) * 3), failure,
                ToolSurfaceStyle.Normal, 16, true, false), used);
        }
        for (int i = used; i < _hostedDrawn; i++) _hosted[i].GameObject.SetActive(false);
        _hostedDrawn = used;
    }

    private int DrawElement(Control control, ToolPanelElement element, int index)
    {
        control.Key = element.Id;
        // Window-local: every hosted control is a child of the panel object, so the window's own screen position is
        // applied by that parent transform and must not be added here a second time.
        ToolInputRect drawn = ToolWindowFrame.ElementBounds(element.Bounds);
        float x = drawn.X;
        float y = drawn.Y;
        float w = Math.Max(1f, drawn.Width);
        float h = Math.Max(1f, drawn.Height);
        control.GameObject.SetActive(true);
        switch (element.Kind)
        {
            case ToolPanelElementKind.Separator:
                SetRect(control.Rect, x, y + h - 2f, w, 2f);
                SetRect(control.Label.GetComponent<RectTransform>(), 0, 0, 1, 1);
                control.Label.text = "";
                control.Image.enabled = true;
                if (!ToolTheme.Apply(control.Image, "header"))
                    control.Image.color = ToolTheme.Native(ToolPalette.Header.Background);
                break;
            case ToolPanelElementKind.Text:
            case ToolPanelElementKind.Status:
                SetRect(control.Rect, x, y, w, h);
                SetRect(control.Label.GetComponent<RectTransform>(), 2, 0, Math.Max(1f, w - 4), h);
                control.Label.text = ToolboxService.Limit(element.Text, 4000);
                control.Label.alignment = TextAnchor.UpperLeft;
                control.Label.fontSize = Math.Max(1, element.Size);
                control.Label.color = ToolTheme.Foreground(
                    element.Kind == ToolPanelElementKind.Status ? "button.disabled" : "button");
                control.Image.enabled = false;
                break;
            case ToolPanelElementKind.Field:
                SetRect(control.Rect, x, y, w, h);
                SetRect(control.Label.GetComponent<RectTransform>(), 10, 0, Math.Max(1f, w - 20), h);
                control.Label.text = element.Text;
                control.Label.alignment = TextAnchor.MiddleLeft;
                control.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
                control.Label.fontSize = Math.Max(1, element.Size);
                control.Label.color = ToolTheme.Foreground("button");
                control.Image.enabled = true;
                // The focused field is where an input method must put its candidate window.
                if (element.Highlighted) _fieldRect = PixelsOf(drawn);
                if (!ToolTheme.Apply(control.Image, element.Highlighted ? "button.selected" : "solid"))
                    control.Image.color = ToolTheme.Native(
                        ToolPalette.ForKey(element.Highlighted ? "button.selected" : "solid").Background);
                break;
            case ToolPanelElementKind.Swatch:
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
                    bool hover = element.Bounds.Contains(_pointerX, _pointerY);
                    bool held = hover && _hostedPressed == element.Id && UnityEngine.Input.GetMouseButton(0);
                    bool primary = element.Style == ToolSurfaceStyle.Primary;
                    bool decorated = primary || element.Style == ToolSurfaceStyle.Undo;
                    bool selected = element.Highlighted || element.Style == ToolSurfaceStyle.Selected;
                    control.Label.color = OfficialButtonSkin.Apply(control.Image, w, h, enabled,
                        selected, hover, held, decorated, primary, navigation: false);
                }
                else if (!ToolTheme.Apply(control.Image, skin))
                    control.Image.color = ToolTheme.Native(ToolPalette.ForKey(skin).Background);
                break;
        }
        return index + 1;
    }

    /// <summary>
    /// Collects this frame's keystrokes for the page's focused field. The buffers are cleared here and read by the
    /// page on its next frame, so a page sees a whole frame's typing as one batch and a page that stops focusing a
    /// field simply stops receiving anything.
    /// </summary>
    private void HandleKeyboard()
    {
        _typed = "";
        _backspace = _submit = _cancel = _delete = false;
        _left = _right = _home = _end = _selectAll = false;
        _composition = "";
        if (_hostedField is null)
        {
            SetIme(false);
            return;
        }
        foreach (char ch in UnityEngine.Input.inputString ?? "")
        {
            if (ch == '\b') _backspace = true;
            else if (ch is '\r' or '\n') _submit = true;
            else if (ch == '\u001b') _cancel = true;
            else if (!char.IsControl(ch) && _typed.Length < 240) _typed += ch;
        }
        bool control = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
        if (control && UnityEngine.Input.GetKeyDown(KeyCode.V))
        {
            string pasted = Clipboard();
            if (pasted.Length != 0 && _typed.Length + pasted.Length <= 480) _typed += pasted;
        }
        if (control && UnityEngine.Input.GetKeyDown(KeyCode.A)) _selectAll = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Delete)) _delete = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) _left = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) _right = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Home)) _home = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.End)) _end = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) _cancel = true;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
            _submit = true;
        SetIme(true);
        _composition = SafeComposition();
    }

    /// <summary>
    /// States whether this window's focused field wants input-method composition, and where its caret is. The switch
    /// itself belongs to <see cref="ToolInputService"/>, which arbitrates it between every surface — including the
    /// drawer, which used to be able to switch composition off under this window's field.
    /// </summary>
    private void SetIme(bool on) => _input.SetComposition(_keyboardOwner, on, _fieldRect);

    private static string SafeComposition()
    {
        try { return UnityEngine.Input.compositionString ?? ""; }
        catch { return ""; }
    }

    private static string Clipboard()
    {
        try { return (UnityEngine.GUIUtility.systemCopyBuffer ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); }
        catch { return ""; }
    }

    private void Publish()
    {
        // The whole window is one rectangle: the title band, the close button, the grip and the page's controls are
        // all inside it, so a click anywhere on the window is ours and never reaches the official editor behind it.
        // A drag or a resize captures every pointer position for as long as it lasts.
        _region.Update(new[] { _interaction.Placement.Rect },
            captureAllPointer: _interaction.IsBusy || _contentDragging);
        _input.SetKeyboardCapture(_keyboardOwner, _hostedField is not null || _releaseKeyboardFrames > 0);
        if (_releaseKeyboardFrames > 0) _releaseKeyboardFrames--;
    }

    private void Show()
    {
        if (_disposed || _shown) return;
        _shown = true;
        _releaseKeyboardFrames = 0;
        // Publish before displaying anything: the click that opened this window must not reach its own body.
        _region.Update(Array.Empty<ToolInputRect>(), captureAllPointer: true);
        // A fresh registration object for every showing. The shared guard keeps a page that threw quarantined for as
        // long as the same registration stays selected — right for a drawer page, which the user leaves by opening
        // another one, and wrong for a window, where closing and reopening it IS how the user retries.
        _showing = new object();
        _guard.Select(_showing, _content, _windowId);
        _guard.Show();
        _canvasObject.SetActive(true);
        _log?.Invoke($"[tool-window] {_windowId} opened");
    }

    private void Hide()
    {
        if (!_shown || _disposed) return;
        _shown = false;
        _interaction.Cancel();
        _hostedPressed = null;
        _hostedField = null;
        _pointerValid = false;
        _input.SetKeyboardCapture(_keyboardOwner, false);
        SetIme(false);
        _guard.Hide();
        _stack.Hide(_windowId);
        _canvasObject.SetActive(false);
        _releaseKeyboardFrames = 2;
        _region.Update(Array.Empty<ToolInputRect>(), captureAllPointer: true);
        _log?.Invoke($"[tool-window] {_windowId} closed");
    }

    /// <summary>
    /// Puts the window back where it opens by default. The caller drops the remembered position; this hands the size
    /// back to the page and makes the next frame resolve the placement again, so a reset is the same gesture whether
    /// the user double-clicked the title band or a mod asked for it. Without this the store was cleared while the
    /// window on screen stayed exactly where the user had left it, still ignoring the page's size.
    /// </summary>
    internal void ResetPlacement()
    {
        _interaction.Cancel();
        _interaction.ForgetUserSize();
        _placementResolved = false;
        _loggedPlacement = "";
    }

    private void Remember(string action)
    {
        ToolWindowPlacement placement = _interaction.Placement;
        _placements.Remember(_windowId, placement);
        _log?.Invoke($"[tool-window] {_windowId} {action}; placement={Describe(placement)}");
        _loggedPlacement = "";
    }

    /// <summary>Publishes where the window really is, once per change, so a report about it can be checked from a log.</summary>
    private void LogPlacementOnce()
    {
        string line = Describe(_interaction.Placement);
        if (string.Equals(line, _loggedPlacement, StringComparison.Ordinal)) return;
        _loggedPlacement = line;
        _log?.Invoke($"[tool-window] {_windowId} placement={line}; controls={_hostedDrawn}");
    }

    /// <summary>Writes down the controls the renderer really put on screen, whenever that set changes.</summary>
    private void LogElementsOnce(IReadOnlyList<ToolPanelElement> elements, int used)
    {
        var signature = new System.Text.StringBuilder();
        foreach (ToolPanelElement element in elements) signature.Append(element.Id).Append('|');
        string ids = signature.ToString();
        if (string.Equals(ids, _hostedIds, StringComparison.Ordinal)) return;
        _hostedIds = ids;
        var text = new System.Text.StringBuilder($"[tool-window] {_windowId} drew {used} controls:");
        for (int i = 0; i < used && i < _hosted.Length; i++)
        {
            RectTransform rect = _hosted[i].Rect;
            text.Append(' ').Append(_hosted[i].Key).Append('@')
                .Append(rect.anchoredPosition.x.ToString("F0")).Append(',')
                .Append(rect.anchoredPosition.y.ToString("F0")).Append(' ')
                .Append(rect.sizeDelta.x.ToString("F0")).Append('x')
                .Append(rect.sizeDelta.y.ToString("F0")).Append(';');
        }
        _log?.Invoke(text.ToString());
    }

    private static string Describe(ToolWindowPlacement placement) =>
        $"{placement.X:F0},{placement.Y:F0} {placement.Width:F0}x{placement.Height:F0}";

    private void ResetInteraction()
    {
        _hostedPressed = null;
        _pointerValid = false;
        _hostedField = null;
        _typed = "";
    }

    private void HideHosted()
    {
        for (int i = 0; i < _hosted.Length; i++) _hosted[i].GameObject.SetActive(false);
        _hostedDrawn = 0;
        _hostedLast = Array.Empty<ToolPanelElement>();
        _hostedIds = "";
        _fieldRect = default;
    }
    /// <summary>Grows the control pool. Doubling keeps the allocation cost off the steady-state frame.</summary>
    private void EnsurePool(int required)
    {
        if (required <= _hosted.Length) return;
        int size = Math.Max(DefaultPoolSize, _hosted.Length);
        while (size < required) size *= 2;
        var grown = new Control[size];
        Array.Copy(_hosted, grown, _hosted.Length);
        for (int i = _hosted.Length; i < size; i++)
        {
            Control control = CreateControl("Hosted" + i, _panel.Rect);
            control.GameObject.SetActive(false);
            grown[i] = control;
        }
        _hosted = grown;
        // The chrome is drawn over the page: a page control that happens to sit under the resize grip must not
        // cover it, or the window could not be resized from where the grip is drawn.
        _grip.GameObject.transform.SetAsLastSibling();
        _close.GameObject.transform.SetAsLastSibling();
    }

    /// <summary>A window-local rectangle in physical screen pixels. The window's scale is one.</summary>
    private ToolInputRect PixelsOf(ToolInputRect windowLocal) =>
        ToolWindowFrame.ToScreenPixels(_interaction.Placement, windowLocal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetInteraction();
        _guard.Hide();
        _stack.Hide(_windowId);
        _input.SetKeyboardCapture(_keyboardOwner, false);
        _region.Dispose();
        SetIme(false);
        try { Object.Destroy(_canvasObject); }
        catch (Exception ex) { _log?.Invoke($"[tool-window] {_windowId} native cleanup failed: {ex.Message}"); }
    }

    private (GameObject GameObject, RectTransform Rect, Image Image) CreateImage(string name, Transform parent, string skin)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.color = ToolTheme.Native(ToolPalette.ForKey(skin).Background);
        ToolTheme.Apply(image, skin);
        return (go, rect, image);
    }

    private Control CreateControl(string name, Transform parent)
    {
        var image = CreateImage(name, parent, "button");
        return new Control(image.GameObject, image.Rect, image.Image,
            CreateText(name + "Label", image.Rect, "", 18, TextAnchor.MiddleCenter));
    }

    private Text CreateText(string name, Transform parent, string value, int size, TextAnchor alignment)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = _font;
        text.text = value;
        text.fontSize = size;
        text.color = _darkText;
        text.alignment = alignment;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private static void SetRect(RectTransform rect, float x, float y, float w, float h)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(w, h);
        rect.localScale = Vector3.one;
    }

    private sealed class Control
    {
        internal readonly GameObject GameObject;
        internal readonly RectTransform Rect;
        internal readonly Image Image;
        internal readonly Text Label;
        internal string Key = "";
        internal Control(GameObject gameObject, RectTransform rect, Image image, Text label)
        { GameObject = gameObject; Rect = rect; Image = image; Label = label; }
    }
}
