extern alias unitycore;

using Rukari.Lib.Tools;
using Color = unitycore::UnityEngine.Color;
using Rect = unitycore::UnityEngine.Rect;
using Sprite = unitycore::UnityEngine.Sprite;
using SpriteMeshType = unitycore::UnityEngine.SpriteMeshType;
using Texture2D = unitycore::UnityEngine.Texture2D;
using TextureFormat = unitycore::UnityEngine.TextureFormat;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector4 = unitycore::UnityEngine.Vector4;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// Uses the runtime's shared skin, with code-drawn icons when that skin is unavailable. Unknown icon ids get a stable,
/// distinct emblem because the accent colour and glyph are derived from the module id.
/// </summary>
public static class RailIconPainter
{
    private const int Size = 64;
    private const float Inset = 2f;
    private const float Corner = 12f;

    private static readonly Dictionary<string, (Texture2D Texture, Sprite Sprite)> Loaded = new(StringComparer.Ordinal);
    private static int _threadId;
    private static Action<string>? _log;

    internal static void Initialize(Action<string> log)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _log = log;
    }

    /// <summary>
    /// Returns a cached icon sprite, or null when the caller must fall back to a text-only button. The game's
    /// bundled atlas emblem is preferred when the verified skin is available; otherwise the procedural glyph is drawn.
    /// </summary>
    public static Sprite? Get(string? iconId, string moduleId)
    {
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return null;
        string glyph = RailIconStyle.GlyphFor(iconId);
        Sprite? emblem = AtlasEmblemSource.Get(glyph);
        if (UiAssetLifetime.IsAlive(emblem)) return emblem;
        string key = glyph + "|" + RailIconStyle.AccentKey(moduleId);
        if (Loaded.TryGetValue(key, out var cached))
        {
            if (UiAssetLifetime.IsAlive(cached.Sprite, cached.Texture)) return cached.Sprite;
            Loaded.Remove(key);
            UiAssetLifetime.Destroy(cached.Sprite);
            UiAssetLifetime.Destroy(cached.Texture);
        }
        Texture2D? texture = null;
        Sprite? sprite = null;
        try
        {
            texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = unitycore::UnityEngine.FilterMode.Bilinear };
            UiAssetLifetime.Retain(texture);
            var pixels = new Color[Size * Size];
            Color accent = Native(RailIconStyle.AccentFor(moduleId));
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float px = x + .5f;
                float py = y + .5f;
                float cover = RoundedCover(px, py);
                // A slightly brighter top edge reads as a raised button without any atlas sprite.
                float shade = .86f + .14f * (py / Size);
                pixels[y * Size + x] = new Color(accent.r * shade, accent.g * shade, accent.b * shade, cover);
            }
            Draw(glyph, pixels, accent, RailIconStyle.Luminance(RailIconStyle.AccentFor(moduleId)) > .45f);
            texture.SetPixels(pixels);
            texture.Apply();
            sprite = Sprite.Create(texture, new Rect(0, 0, Size, Size), new Vector2(.5f, .5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(Corner, Corner, Corner, Corner));
            UiAssetLifetime.Retain(sprite);
            Loaded[key] = (texture, sprite);
            return sprite;
        }
        catch (Exception ex)
        {
            UiAssetLifetime.Destroy(sprite);
            UiAssetLifetime.Destroy(texture);
            _log?.Invoke($"Rail icon '{glyph}' unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Stable accent colour per module so two unknown mods never look identical.</summary>
    public static ToolColor Accent(string? moduleId) => RailIconStyle.AccentFor(moduleId);

    private static float RoundedCover(float px, float py)
    {
        float min = Inset + Corner;
        float max = Size - Inset - Corner;
        float dx = px - Math.Clamp(px, min, max);
        float dy = py - Math.Clamp(py, min, max);
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float inside = Math.Clamp(Corner + .5f - distance, 0f, 1f);
        bool withinBox = px >= Inset && px <= Size - Inset && py >= Inset && py <= Size - Inset;
        return withinBox ? inside : 0f;
    }

    private static void Draw(string glyph, Color[] pixels, Color accent, bool darkGlyph)
    {
        Color glyphColor = darkGlyph ? new Color(.08f, .10f, .14f, 1f) : new Color(.97f, .98f, 1f, 1f);
        switch (glyph)
        {
            case "wave":
                Box(pixels, 15, 27, 4, 10, glyphColor);
                Box(pixels, 22, 21, 4, 22, glyphColor);
                Box(pixels, 29, 15, 4, 34, glyphColor);
                Box(pixels, 36, 21, 4, 22, glyphColor);
                Box(pixels, 43, 27, 4, 10, glyphColor);
                break;
            case "camera":
                Box(pixels, 14, 22, 36, 22, glyphColor);
                Box(pixels, 24, 17, 16, 5, glyphColor);
                Disc(pixels, 32, 33, 7.5f, accent);
                break;
            case "card":
                Box(pixels, 16, 20, 32, 24, glyphColor);
                Box(pixels, 16, 26, 32, 2, accent);
                Box(pixels, 21, 33, 6, 6, accent);
                Box(pixels, 31, 33, 12, 2, accent);
                Box(pixels, 31, 37, 9, 2, accent);
                break;
            case "magnify":
                Disc(pixels, 28, 36, 11f, glyphColor);
                Disc(pixels, 28, 36, 7f, accent);
                Box(pixels, 36, 18, 5, 16, glyphColor);
                break;
            case "master":
                // The rail's own entry: a phone body with a chat bubble, matching the atlas emblem the user
                // picked. This is only drawn when no atlas export is available, but it must not be a gear.
                Box(pixels, 22, 12, 20, 40, glyphColor);
                Box(pixels, 25, 16, 14, 26, accent);
                Box(pixels, 27, 45, 10, 3, accent);
                Box(pixels, 36, 36, 14, 10, glyphColor);
                Box(pixels, 39, 39, 8, 2, accent);
                Box(pixels, 39, 43, 5, 2, accent);
                break;
            default: // gear
                Disc(pixels, 32, 32, 12f, glyphColor);
                Disc(pixels, 32, 32, 5f, accent);
                Box(pixels, 29, 14, 6, 36, glyphColor);
                Box(pixels, 14, 29, 36, 6, glyphColor);
                break;
        }
    }

    private static void Box(Color[] pixels, int x, int y, int width, int height, Color color)
    {
        for (int iy = y; iy < y + height; iy++)
        for (int ix = x; ix < x + width; ix++)
            Over(pixels, ix, iy, color);
    }

    private static void Disc(Color[] pixels, float centreX, float centreY, float radius, Color color)
    {
        for (int iy = (int)(centreY - radius - 1); iy <= centreY + radius + 1; iy++)
        for (int ix = (int)(centreX - radius - 1); ix <= centreX + radius + 1; ix++)
        {
            float dx = ix + .5f - centreX;
            float dy = iy + .5f - centreY;
            if (dx * dx + dy * dy <= radius * radius) Over(pixels, ix, iy, color);
        }
    }

    private static void Over(Color[] pixels, int x, int y, Color color)
    {
        if (x < 0 || y < 0 || x >= Size || y >= Size) return;
        int index = y * Size + x;
        if (pixels[index].a <= .35f) return;
        pixels[index] = new Color(color.r, color.g, color.b, pixels[index].a);
    }

    private static Color Native(ToolColor color) => new(color.R, color.G, color.B, 1f);

    internal static void Shutdown()
    {
        if (_threadId != Environment.CurrentManagedThreadId) return;
        foreach (var asset in Loaded.Values)
        {
            UiAssetLifetime.Destroy(asset.Sprite);
            UiAssetLifetime.Destroy(asset.Texture);
        }
        Loaded.Clear();
        _threadId = 0;
        _log = null;
    }
}
