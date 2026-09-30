extern alias unitycore;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Component = unitycore::UnityEngine.Component;
using GameObject = unitycore::UnityEngine.GameObject;
using Transform = unitycore::UnityEngine.Transform;
using UnityObject = unitycore::UnityEngine.Object;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace Rukari.Lib.Runtime.Settings;

/// <summary>
/// Adds one owned NGUI row after the game's Mod manager row. This adapter reads only visual
/// properties from the live template; no official GameObject, behaviour or event list is cloned.
/// The signatures are checked against AAfix4 interop; the native path still requires user validation.
/// </summary>
internal sealed class OfficialSettingsEntry : IDisposable
{
    private const string EntryName = "RukariModSettingsEntry";
    private readonly Action _open;
    private readonly Action<string> _log;
    private GameObject? _row;
    private Transform? _popup;
    private Transform? _tableRoot;
    private GameObject? _source;
    private UIEventListener? _listener;
    private UIEventListener.VoidDelegate? _clickDelegate;
    private long _nextScan;
    private IntPtr _failedSource;
    private bool _loggedFailure;
    private bool _disposed;

    public OfficialSettingsEntry(Action open, Action<string> log)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsSettingsVisible
    {
        get
        {
            if (_disposed) return false;
            try { return IsVisible(_popup); }
            catch { return false; }
        }
    }

    public void Tick()
    {
        if (_disposed || Environment.TickCount64 < _nextScan) return;
        _nextScan = Environment.TickCount64 + 500;
        try
        {
            // Cached wrappers alone do not establish that native Unity objects still exist.
            if (Alive(_row) && Alive(_source) && Alive(_popup)) return;
            RemoveOwnedRow();
            var panels = UnityObject.FindObjectsOfType(Il2CppType.Of<SettingPanel>());
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = panels[i]?.TryCast<SettingPanel>();
                if (!Alive(panel) || !panel!.gameObject.activeInHierarchy) continue;
                GameObject? source = panel.modManagerEntry;
                if (!Alive(source) || !source!.activeInHierarchy) continue; // official row may not exist yet
                Transform? popup = FindSettingsPopup(source.transform);
                if (!IsVisible(popup)) continue;
                _popup = popup;
                if (_failedSource == source.Pointer) return;
                _failedSource = source.Pointer; // Do not repeat a failing native construction every frame.
                CreateRow(source);
                _failedSource = IntPtr.Zero;
                return;
            }
        }
        catch (Exception ex)
        {
            RemoveOwnedRow();
            LogFailure($"入口保持关闭：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CreateRow(GameObject source)
    {
        Transform parent = source.transform.parent;
        var table = Get<UITable>(parent.gameObject);
        if (!Alive(table) || table!.columns != 1 || table.direction != UITable.Direction.Down || table.inverted
            || (table.sorting != UITable.Sorting.None && table.sorting != UITable.Sorting.Vertical))
            throw new InvalidOperationException("Mod 管理行未位于受支持的纵向设置列表中。");
        if (Alive(parent.Find(EntryName)))
            throw new InvalidOperationException("设置列表已有同名入口，拒绝重复添加。");

        // These exact local paths are visible component names, not localised text guesses.
        Transform caption = RequiredChild(source.transform, "Caption");
        Transform description = RequiredChild(source.transform, "Description");
        Transform button = RequiredChild(source.transform, "EditBtn");
        Transform buttonLabel = RequiredChild(button, "Label_Locale");
        Transform buttonBackground = RequiredChild(button, "BG");
        if (!Alive(Get<UILabel>(caption.gameObject)) || !Alive(Get<UILabel>(description.gameObject))
            || !Alive(Get<UILabel>(buttonLabel.gameObject)) || !Alive(Get<UISprite>(buttonBackground.gameObject)))
            throw new InvalidOperationException("官方设置行的标题、说明或按钮样式不完整。");

        _source = source;
        _tableRoot = parent;
        _row = new GameObject(EntryName);
        _row.SetActive(false);
        _row.layer = source.layer;
        _row.transform.SetParent(parent, false);
        _row.transform.localPosition = source.transform.localPosition + new Vector3(0, -1, 0);
        _row.transform.localRotation = source.transform.localRotation;
        _row.transform.localScale = source.transform.localScale;
        _row.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);

        var nodes = new Dictionary<IntPtr, Transform> { [source.transform.Pointer] = _row.transform };
        var pairs = new List<(Transform Source, Transform Target)> { (source.transform, _row.transform) };
        CopyHierarchy(source.transform, _row.transform, nodes, pairs);
        var widgets = new List<(UIWidget Source, UIWidget Target)>();
        foreach (var pair in pairs)
        {
            UIWidget? original = Get<UIWidget>(pair.Source.gameObject);
            if (!Alive(original)) continue;
            UIWidget copy;
            if (original!.TryCast<UILabel>() is { } label)
            {
                var target = Add<UILabel>(pair.Target.gameObject);
                CopyLabel(label, target);
                copy = target;
            }
            else if (original.TryCast<UISprite>() is { } sprite)
            {
                var target = Add<UISprite>(pair.Target.gameObject);
                CopySprite(sprite, target);
                copy = target;
            }
            else
            {
                // A base UIWidget supplies layout bounds without a draw call. Other widget kinds
                // cannot be silently approximated (UITexture, UI2DSprite, etc.).
                if (original.GetIl2CppType().FullName != "UIWidget")
                    throw new InvalidOperationException("官方设置行出现不支持的视觉组件。");
                copy = Add<UIWidget>(pair.Target.gameObject);
            }
            CopyWidget(original, copy);
            widgets.Add((original, copy));
        }
        foreach (var pair in widgets) CopyAnchors(pair.Source, pair.Target, nodes);

        Get<UILabel>(nodes[caption.Pointer].gameObject)!.text = "模组设置";
        Get<UILabel>(nodes[description.Pointer].gameObject)!.text = "设置各模组的全局选项";
        Get<UILabel>(nodes[buttonLabel.Pointer].gameObject)!.text = "打开";

        GameObject ownedButton = nodes[button.Pointer].gameObject;
        var feedback = Add<UIButton>(ownedButton);
        feedback.tweenTarget = nodes[buttonBackground.Pointer].gameObject;
        var originalFeedback = Get<UIButtonColor>(button.gameObject);
        if (Alive(originalFeedback))
        {
            feedback.hover = originalFeedback!.hover;
            feedback.pressed = originalFeedback.pressed;
            feedback.disabledColor = originalFeedback.disabledColor;
            feedback.duration = originalFeedback.duration;
        }
        // Fresh native callback containers: none of the original button's onClick/localiser scripts
        // or target references are shared with our entry.
        feedback.onClick = new Il2CppSystem.Collections.Generic.List<EventDelegate>();
        _listener = Add<UIEventListener>(ownedButton);
        _clickDelegate = DelegateSupport.ConvertDelegate<UIEventListener.VoidDelegate>((Action<GameObject>)(_ =>
        {
            if (!IsSettingsVisible) return;
            try { _open(); }
            catch (Exception ex) { LogFailure($"打开设置失败：{ex.GetType().Name}: {ex.Message}"); }
        }));
        _listener.onClick = _clickDelegate;
        _listener.onSubmit = _clickDelegate;
        NGUITools.AddWidgetCollider(ownedButton, true);

        // An unrendered widget contributes bottom breathing room to the content bounds.
        float bottom = 0;
        foreach (var pair in widgets)
        {
            var corners = pair.Target.localCorners;
            for (int i = 0; i < corners.Length; i++)
            {
                float y = _row.transform.InverseTransformPoint(pair.Target.transform.TransformPoint(corners[i])).y;
                bottom = Math.Min(bottom, y);
            }
        }
        var spacer = new GameObject("BottomSpacing");
        spacer.SetActive(false);
        spacer.layer = _row.layer;
        spacer.transform.SetParent(_row.transform, false);
        spacer.transform.localPosition = new Vector3(0, bottom - 12, 0);
        var bounds = Add<UIWidget>(spacer);
        bounds.width = 1;
        bounds.height = 24;
        bounds.pivot = UIWidget.Pivot.Center;
        spacer.SetActive(true);
        _row.SetActive(true);
        Reflow(parent);
        _log("[settings-entry] 已添加模组设置入口；Native=awaiting-user-validation。");
    }

    private static void CopyHierarchy(Transform source, Transform target, Dictionary<IntPtr, Transform> nodes,
        List<(Transform Source, Transform Target)> pairs)
    {
        for (int i = 0; i < source.childCount; i++)
        {
            if (pairs.Count >= 64) throw new InvalidOperationException("官方设置行层级超出支持范围。");
            Transform child = source.GetChild(i);
            var copy = new GameObject(child.gameObject.name);
            copy.SetActive(false);
            copy.layer = child.gameObject.layer;
            copy.transform.SetParent(target, false);
            copy.transform.localPosition = child.localPosition;
            copy.transform.localRotation = child.localRotation;
            copy.transform.localScale = child.localScale;
            nodes.Add(child.Pointer, copy.transform);
            pairs.Add((child, copy.transform));
            CopyHierarchy(child, copy.transform, nodes, pairs);
            copy.SetActive(child.gameObject.activeSelf); // the owned root stays inactive until fully wired
        }
    }

    private static void CopyWidget(UIWidget source, UIWidget target)
    {
        // The hierarchy already has the template's local coordinates. The ordinary pivot
        // setter also moves the transform to preserve its old corners, which changes the
        // bounds used by UITable before the copied anchors settle. Copy the pivot only.
        target.rawPivot = source.pivot;
        target.width = source.width;
        target.height = source.height;
        target.depth = source.depth;
        target.color = source.color;
        target.enabled = source.enabled;
        target.boundless = source.boundless;
        target.autoResizeBoxCollider = false;
        target.hideIfOffScreen = source.hideIfOffScreen;
        target.keepAspectRatio = source.keepAspectRatio;
        target.aspectRatio = source.aspectRatio;
    }

    private static void CopyLabel(UILabel source, UILabel target)
    {
        target.trueTypeFont = source.trueTypeFont;
        target.bitmapFont = source.bitmapFont;
        target.fontSize = source.fontSize;
        target.fontStyle = source.fontStyle;
        target.alignment = source.alignment;
        target.overflowMethod = source.overflowMethod;
        target.maxLineCount = source.maxLineCount;
        target.supportEncoding = source.supportEncoding;
        target.spacingX = source.spacingX;
        target.spacingY = source.spacingY;
        target.effectStyle = source.effectStyle;
        target.effectColor = source.effectColor;
        target.effectDistance = source.effectDistance;
        target.text = source.text;
    }

    private static void CopySprite(UISprite source, UISprite target)
    {
        target.atlas = source.atlas;
        target.spriteName = source.spriteName;
        target.type = source.type;
        target.flip = source.flip;
        target.fillCenter = source.fillCenter;
        target.fixedAspect = source.fixedAspect;
        target.inclineAngle = source.inclineAngle;
        target.applyGradient = source.applyGradient;
        target.gradientTop = source.gradientTop;
        target.gradientBottom = source.gradientBottom;
    }

    private static void CopyAnchors(UIRect source, UIRect target, Dictionary<IntPtr, Transform> nodes)
    {
        CopyAnchor(source.leftAnchor, target.leftAnchor, nodes);
        CopyAnchor(source.rightAnchor, target.rightAnchor, nodes);
        CopyAnchor(source.bottomAnchor, target.bottomAnchor, nodes);
        CopyAnchor(source.topAnchor, target.topAnchor, nodes);
        target.updateAnchors = source.updateAnchors;
    }

    private static void CopyAnchor(UIRect.AnchorPoint source, UIRect.AnchorPoint target, Dictionary<IntPtr, Transform> nodes)
    {
        Transform? original = source.target;
        if (Alive(original))
        {
            // Row-local anchors must point to the new row; binding the original title or button
            // would pull our visuals back on top of Mod 管理 after a layout refresh.
            if (!nodes.TryGetValue(original!.Pointer, out Transform? mapped))
                throw new InvalidOperationException("官方设置行包含外部布局锚点，拒绝错误定位。");
            target.target = mapped;
        }
        target.relative = source.relative;
        target.absolute = source.absolute;
    }

    private static void Reflow(Transform? start)
    {
        for (int i = 0; i < 24 && Alive(start); i++, start = start!.parent)
        {
            var table = Get<UITable>(start!.gameObject);
            if (Alive(table)) table!.Reposition();
            var scroll = Get<UIScrollView>(start.gameObject);
            if (Alive(scroll))
            {
                scroll!.InvalidateBounds();
                scroll.UpdateScrollbars(true);
            }
            if (start.gameObject.name == "UIPopup_Settings") return;
        }
    }

    private static Transform RequiredChild(Transform parent, string path) => parent.Find(path)
        ?? throw new InvalidOperationException($"官方设置行缺少 {path}。");

    private static Transform? FindSettingsPopup(Transform source)
    {
        Transform? node = source;
        for (int i = 0; i < 24 && Alive(node); i++, node = node!.parent)
            if (node!.gameObject.name == "UIPopup_Settings") return node;
        return null;
    }

    private static bool IsVisible(Transform? node)
    {
        if (!Alive(node) || !node!.gameObject.activeInHierarchy) return false;
        for (int i = 0; i < 48 && Alive(node); i++, node = node!.parent)
        {
            var panel = Get<UIPanel>(node!.gameObject);
            if (Alive(panel) && (!panel!.enabled || !float.IsFinite(panel.alpha) || panel.alpha <= .001f)) return false;
        }
        return node == null;
    }

    private static bool Alive(UnityObject? value) => value is not null && !value.WasCollected
        && value.Pointer != IntPtr.Zero && value != null;

    private static T? Get<T>(GameObject gameObject) where T : Component =>
        gameObject.GetComponent(Il2CppType.Of<T>())?.TryCast<T>();

    private static T Add<T>(GameObject gameObject) where T : Component =>
        gameObject.AddComponent(Il2CppType.Of<T>()).Cast<T>();

    private void LogFailure(string reason)
    {
        if (_loggedFailure) return;
        _loggedFailure = true;
        _log("[settings-entry] " + reason);
    }

    private void RemoveOwnedRow()
    {
        Transform? parent = _tableRoot;
        _tableRoot = null;
        _source = null;
        try
        {
            if (Alive(_listener)) { _listener!.onClick = null; _listener.onSubmit = null; }
            if (Alive(_row))
            {
                _row!.SetActive(false);
                // Remove it from the layout before delayed Unity destruction.
                _row.transform.SetParent(null, false);
                UnityObject.Destroy(_row);
            }
            if (Alive(parent)) Reflow(parent);
        }
        catch (Exception ex) { LogFailure($"清理入口时界面已失效：{ex.GetType().Name}: {ex.Message}"); }
        finally { _row = null; _listener = null; _clickDelegate = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveOwnedRow();
        _popup = null;
    }
}
