extern alias unitycore;
extern alias unitytext;
extern alias unityui;

using Rukari.Lib.Tools;
using Color = unitycore::UnityEngine.Color;
using Image = unityui::UnityEngine.UI.Image;
using Object = unitycore::UnityEngine.Object;
using Rect = unitycore::UnityEngine.Rect;
using RectTransform = unitycore::UnityEngine.RectTransform;
using Sprite = unitycore::UnityEngine.Sprite;
using SpriteMeshType = unitycore::UnityEngine.SpriteMeshType;
using Text = unityui::UnityEngine.UI.Text;
using TextAnchor = unitytext::UnityEngine.TextAnchor;
using Texture2D = unitycore::UnityEngine.Texture2D;
using TextureFormat = unitycore::UnityEngine.TextureFormat;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector3 = unitycore::UnityEngine.Vector3;
using Vector4 = unitycore::UnityEngine.Vector4;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// Unity-only rendering adapter for tools which still own complex native views. This is deliberately
/// in Runtime, not the portable Rukari.Lib contract. Call only on the captured game main thread.
/// </summary>
public static class ToolTheme
{
    /// <summary>
    /// Draws the shared inclined button for a page that owns its native controls. The explicit role flags keep
    /// decoration independent of size: normal actions are plain, primary/secondary commit actions may carry art.
    /// Returns the foreground that belongs with this state. All arguments are required to keep consumer calls
    /// explicit; existing public methods and signatures are unchanged.
    /// </summary>
    public static Color ApplyButton(Image image, float width, float height, bool enabled, bool highlighted,
        bool pressed, bool decorated, bool primary) =>
        OfficialButtonSkin.Apply(image, width, height, enabled, highlighted, pressed, decorated, primary);

    /// <summary>
    /// Applies the neutral control palette with separate persistent selection and hover feedback. Navigation
    /// uses a slightly bluer neutral than parameter controls. The original eight-argument entry point remains
    /// available for already compiled providers; every argument of this new overload is also required.
    /// </summary>
    public static Color ApplyButton(Image image, float width, float height, bool enabled, bool selected, bool hovered,
        bool pressed, bool decorated, bool primary, bool navigation) =>
        OfficialButtonSkin.Apply(image, width, height, enabled, selected, hovered, pressed, decorated, primary, navigation);

    /// <summary>
    /// Centres a single-line button label on the inclined face, excluding its lower shadow. The current
    /// Microsoft YaHei UI font also needs a small optical lift: this was measured in the user's screenshots,
    /// not read from a new native glyph/vertex API. Keep the original text height so short buttons do not lose
    /// their line to vertical truncation. Only label geometry changes; the button and its hit area stay put.
    /// </summary>
    public static void CenterButtonLabel(Text label, float width, float height, float horizontalInset)
    {
        float shadowLift = height * .07f; // The painted face excludes the bottom 14% of the sprite.
        float fontLift = label.fontSize * .15f;
        float skew = Math.Min(width * .20f, height * .17632698f);
        var rect = label.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(horizontalInset + skew * .07f, shadowLift + fontLift);
        rect.sizeDelta = new Vector2(Math.Max(1f, width - horizontalInset * 2f), height);
        rect.localScale = Vector3.one;
        label.alignment = TextAnchor.MiddleCenter;
    }

    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
        { "panel", "header", "button", "button.disabled", "button.selected", "button.primary", "button.undo", "tab", "solid", "information",
          "panel.card", "panel.cardHeader", "scroll.track", "scroll.thumb" };
    private static readonly Dictionary<bool, (Texture2D Texture, Sprite Sprite)> Loaded = new();
    private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);
    private static int _threadId;
    private static Action<string>? _log;

    internal static void Initialize(Action<string> log)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _log = log;
        Failed.Clear();
    }

    /// <summary>Applies a white rectangular primitive tinted with the matching palette surface. False leaves the image unchanged.</summary>
    public static bool Apply(Image image, string key)
    {
        if (_threadId != Environment.CurrentManagedThreadId || image is null || !Keys.Contains(key))
            return false;
        if (Failed.Contains(key)) return false;
        // A scrollbar track is a long thin bar: a rounded corner would turn it into a pill, so it shares the square
        // primitive with the flat surfaces instead of the nine-sliced one.
        bool rounded = key is not ("solid" or "header" or "scroll.track");
        if (!Loaded.TryGetValue(rounded, out var asset))
        {
            Texture2D? texture = null;
            Sprite? sprite = null;
            try
            {
                const int size = 24;
                float corner = rounded ? 5f : 0f;
                texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + .5f - Math.Clamp(x + .5f, corner, size - corner);
                    float dy = y + .5f - Math.Clamp(y + .5f, corner, size - corner);
                    float alpha = corner == 0 ? 1 : Math.Clamp(corner + .5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                    texture.SetPixel(x, y, new Color(1, 1, 1, alpha));
                }
                texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f),
                    100f, 0, SpriteMeshType.FullRect, new Vector4(corner, corner, corner, corner));
                asset = (texture, sprite);
                Loaded.Add(rounded, asset);
            }
            catch (Exception ex)
            {
                if (sprite is not null) Object.Destroy(sprite);
                if (texture is not null) Object.Destroy(texture);
                Failed.Add(key);
                _log?.Invoke($"Tool skin '{key}' unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
        image.sprite = asset.Sprite;
        image.type = rounded ? Image.Type.Sliced : Image.Type.Simple;
        image.preserveAspect = false;
        image.pixelsPerUnitMultiplier = 1f;
        image.color = Native(ToolPalette.ForKey(key).Background);
        return true;
    }

    internal static Color Native(ToolColor color) => new(color.R, color.G, color.B, 1);

    /// <summary>
    /// Parses a style sheet's <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. A hosted page asks the theme for a colour rather
    /// than converting one by hand, so a swatch and the thing it configures can never end up different shades.
    /// </summary>
    internal static Color ParseColour(string? value, Color fallback)
    {
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        if (text.Length is not (6 or 8)) return fallback;
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint packed)) return fallback;
        return text.Length == 6
            ? new Color(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f)
            : new Color(((packed >> 24) & 0xFF) / 255f, ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f);
    }

    internal static Color Foreground(string key) => Native(ToolPalette.ForKey(key).Foreground);

    internal static void Shutdown()
    {
        if (_threadId != Environment.CurrentManagedThreadId) return;
        foreach (var asset in Loaded.Values)
        {
            Object.Destroy(asset.Sprite);
            Object.Destroy(asset.Texture);
        }
        Loaded.Clear();
        Failed.Clear();
        _threadId = 0;
        _log = null;
    }
}
