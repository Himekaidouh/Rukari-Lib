extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using Rukari.Lib.Tools;
using Color = unitycore::UnityEngine.Color;
using GameObject = unitycore::UnityEngine.GameObject;
using Image = unityui::UnityEngine.UI.Image;
using RectTransform = unitycore::UnityEngine.RectTransform;
using Sprite = unitycore::UnityEngine.Sprite;
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// The settings window's own nine-sliced sheet and corner decorations. This owns only passive Images, created
/// before the page's controls; input, panel size and content coordinates remain owned by the existing drawer.
/// The measured separator is at source y=132 (not at the 145-pixel slice boundary). Using that distinction keeps
/// the header inside the space already reserved by a page without moving any of its controls.
/// </summary>
internal sealed class OfficialPopupFrame
{
    internal static readonly Color Heading = new(45f / 255f, 70f / 255f, 100f / 255f, 1f);
    private readonly Image _background;
    private readonly (RectTransform Rect, Image Image) _top;
    private readonly (RectTransform Rect, Image Image) _corner;
    private readonly (RectTransform Rect, Image Image) _underline;

    internal OfficialPopupFrame(RectTransform parent, Image background)
    {
        _background = background;
        _top = Create("PopupTopLeft", parent);
        _corner = Create("PopupBottomRight", parent);
        _underline = Create("PopupTitleUnderline", parent);
    }

    internal static void CenterTitle(Text label, float headerBottom, float headerHeight)
    {
        var rect = label.GetComponent<RectTransform>();
        // Centre in the visible band, not the content area's top padding. As with button labels, the current
        // Microsoft YaHei UI font needs an optical lift; retain the text box height to avoid line truncation.
        float fontLift = label.fontSize * .15f;
        rect.anchoredPosition = new Vector2(rect.anchoredPosition.x,
            headerBottom + (headerHeight - rect.sizeDelta.y) / 2f + fontLift);
        label.alignment = TextAnchor.MiddleCenter;
    }

    internal void Layout(float width, float height, float headerHeight, ToolInputRect title, float textWidth)
    {
        float sourceScale = headerHeight / 132f;
        Sprite? sheet = AtlasEmblemSource.GetSprite("Common:Common_Popup_Bg");
        if (sheet is not null)
        {
            _background.sprite = sheet;
            _background.type = Image.Type.Sliced;
            _background.preserveAspect = false;
            _background.pixelsPerUnitMultiplier = 1f / sourceScale;
            _background.color = new Color(248f / 255f, 248f / 255f, 248f / 255f, 1f);
        }
        else
        {
            _background.pixelsPerUnitMultiplier = 1f;
            ToolTheme.Apply(_background, "panel");
        }
        _background.enabled = true;

        // The source art already fades to alpha zero and follows the popup's corners. Preserve that alpha and
        // its anchors instead of tinting a full-width band or adding a new mask to the page's controls.
        float topScale = Math.Min(sourceScale, Math.Max(1f, width - 6f) / 494f);
        Draw(_top, sheet is null ? null : AtlasEmblemSource.GetDecoration("Popup_Img_Deco_2"),
            5f * sourceScale, height - 133f * topScale, 494f * topScale, 132f * topScale, Color.white);
        float cornerScale = Math.Min(sourceScale,
            Math.Min(width / 2398f, Math.Max(0f, height - headerHeight - 25f * sourceScale) / 692f));
        Draw(_corner, sheet is null ? null : AtlasEmblemSource.GetDecoration("Popup_Img_Deco_1"),
            width - 3f * sourceScale - 1458f * cornerScale, 25f * sourceScale,
            1458f * cornerScale, 692f * cornerScale, Color.white);

        float underlineWidth = Math.Min(title.Width, Math.Max(16f, textWidth + 14f * sourceScale));
        // The square sprite is only a solid fill. Null is also a solid uGUI Image, so the title stays recognizable
        // on a machine that has not exported the atlas.
        _underline.Image.sprite = AtlasEmblemSource.GetSprite("Common:Common_Square_Bg", sliced: false);
        _underline.Image.type = Image.Type.Simple;
        _underline.Image.preserveAspect = false;
        _underline.Image.color = new Color(1f, .9342736f, 0f, .9372549f);
        _underline.Image.enabled = true;
        SetRect(_underline.Rect, (width - underlineWidth) / 2f, height - headerHeight + sourceScale,
            underlineWidth, Math.Max(2f, 8f * sourceScale));
    }

    private static void Draw((RectTransform Rect, Image Image) layer, Sprite? sprite,
        float x, float y, float width, float height, Color color)
    {
        layer.Image.enabled = sprite is not null && width > 1f && height > 1f;
        if (!layer.Image.enabled) return;
        layer.Image.sprite = sprite;
        layer.Image.type = Image.Type.Simple;
        layer.Image.preserveAspect = false;
        layer.Image.color = color;
        SetRect(layer.Rect, x, y, width, height);
    }

    private static (RectTransform Rect, Image Image) Create(string name, RectTransform parent)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.enabled = false;
        return (rect, image);
    }

    private static void SetRect(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
        rect.localScale = Vector3.one;
    }
}
