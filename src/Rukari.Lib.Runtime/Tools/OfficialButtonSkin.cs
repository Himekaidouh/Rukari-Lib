extern alias unitycore;
extern alias unityui;

using Color = unitycore::UnityEngine.Color;
using FilterMode = unitycore::UnityEngine.FilterMode;
using Image = unityui::UnityEngine.UI.Image;
using Object = unitycore::UnityEngine.Object;
using Rect = unitycore::UnityEngine.Rect;
using Sprite = unitycore::UnityEngine.Sprite;
using SpriteMeshType = unitycore::UnityEngine.SpriteMeshType;
using Texture2D = unitycore::UnityEngine.Texture2D;
using TextureFormat = unitycore::UnityEngine.TextureFormat;
using TextureWrapMode = unitycore::UnityEngine.TextureWrapMode;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector4 = unitycore::UnityEngine.Vector4;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// The verified settings-button recipe, rendered in an existing uGUI Image: a nine-sliced plate with its own
/// shadow, sheared by ten degrees, with optional facet art at both ends. Decoration follows the control's role,
/// never a height threshold. No NGUI calls, injected components or custom shaders are involved.
/// Source pixels and size-bucketed sprites are cached. This is a sampled reconstruction, not pixel-identical NGUI.
/// </summary>
internal static class OfficialButtonSkin
{
    private const string SmallPlate = "Common:Common_Bg_Raius5px_Shadow";
    private const string LargePlate = "Common:Common_Btn_BG";
    private const string BlueFacet = "Common:Common_Btn_Normal_B_C_Pt";
    private const string YellowFacet = "Common:Common_Btn_Normal_Y_S_Pt";
    private const int Bucket = 4;
    private const int MaximumWidth = 768;
    private const int MaximumHeight = 128;
    private const int MaximumPixels = 65536;
    private const int MaximumVariantsPerStyle = 20;
    private const float Shear = .17632698f; // tan(10 degrees)

    private static readonly Color Navy = new(45f / 255f, 70f / 255f, 100f / 255f, 1f);
    private static readonly Dictionary<AssetKey, Asset> Assets = new();
    private static readonly Dictionary<string, Pixels?> Sources = new(StringComparer.Ordinal);
    private static readonly HashSet<AssetKey> Failed = new();
    private static int _threadId;
    private static Action<string>? _log;
    private static bool _uploadUnavailable;

    private readonly record struct AssetKey(int Width, int Height, bool Decorated, bool Primary);
    private sealed record Asset(Texture2D Texture, Sprite Sprite);
    private sealed record Pixels(int Width, int Height, Color[] Data, Vector4 Border);

    internal static void Initialize(Action<string> log)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _log = log;
    }

    /// <summary>Width/height are dimensions in caller canvas units. Returns the matching foreground.</summary>
    internal static Color Apply(Image image, float width, float height, bool enabled, bool highlighted,
        bool pressed, bool decorated = false, bool primary = false) =>
        Apply(image, width, height, enabled, highlighted, false, pressed, decorated, primary, false);

    /// <summary>
    /// Dense tools use neutral faces; only their persistent selection receives the cyan accent. Hover remains
    /// separate from selection. These tints reuse one white sprite, so pointer movement creates no new textures.
    /// Decorated commit actions keep their existing baked colours and state treatment.
    /// </summary>
    internal static Color Apply(Image image, float width, float height, bool enabled, bool selected, bool hovered,
        bool pressed, bool decorated, bool primary, bool navigation)
    {
        Color foreground = enabled ? Navy : decorated ? new Color(.34f, .39f, .44f, 1f)
            : new Color(91f / 255f, 107f / 255f, 122f / 255f, 1f);
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return foreground;
        Color tint = decorated
            ? !enabled ? new Color(.82f, .84f, .86f, .60f)
                : pressed ? new Color(.70f, .80f, .90f, 1f)
                : selected || hovered ? new Color(.82f, .92f, 1f, 1f) : Color.white
            : PlainTint(enabled, selected, hovered, pressed, navigation);
        Sprite? sprite = GetOrCreate(KeyFor(width, height, decorated, primary));
        if (sprite is null)
        {
            Color fallback = ToolPlateSkin.Apply(image, enabled, selected || hovered, 1f);
            if (decorated) return fallback;
            image.color = tint;
            return foreground;
        }

        image.enabled = true;
        image.sprite = sprite;
        image.type = Image.Type.Simple;
        image.preserveAspect = false;
        image.pixelsPerUnitMultiplier = 1f;
        // Rukari's state adaptation, not an assertion about unverified native button-state colours.
        image.color = tint;
        return foreground;
    }

    private static Color PlainTint(bool enabled, bool selected, bool hovered, bool pressed, bool navigation)
    {
        if (!enabled) return new Color(225f / 255f, 228f / 255f, 231f / 255f, 1f);
        if (pressed) return selected ? new Color(102f / 255f, 197f / 255f, 232f / 255f, 1f)
            : new Color(188f / 255f, 216f / 255f, 233f / 255f, 1f);
        if (selected) return new Color(119f / 255f, 222f / 255f, 1f, 1f);
        if (hovered) return new Color(213f / 255f, 231f / 255f, 242f / 255f, 1f);
        return navigation ? new Color(231f / 255f, 238f / 255f, 244f / 255f, 1f)
            : new Color(237f / 255f, 241f / 255f, 244f / 255f, 1f);
    }

    private static AssetKey KeyFor(float width, float height, bool decorated, bool primary)
    {
        if (!float.IsFinite(width) || width <= 0f) width = 80f;
        if (!float.IsFinite(height) || height <= 0f) height = 32f;
        float scale = Math.Min(1f, Math.Min(MaximumWidth / width, MaximumHeight / height));
        width *= scale;
        height *= scale;
        if (width * height > MaximumPixels)
        {
            scale = MathF.Sqrt(MaximumPixels / (width * height));
            width *= scale;
            height *= scale;
        }
        int w = Math.Clamp((int)MathF.Round(width / Bucket) * Bucket, 8, MaximumWidth);
        int h = Math.Clamp((int)MathF.Round(height / Bucket) * Bucket, 8, MaximumHeight);
        while (w * h > MaximumPixels) w -= Bucket;
        return new AssetKey(w, h, decorated, primary);
    }

    private static Sprite? GetOrCreate(AssetKey key)
    {
        if (Assets.TryGetValue(key, out Asset? cached)) return cached.Sprite;
        Asset? nearest = null;
        float bestDistance = float.MaxValue;
        int sameStyle = 0;
        foreach (var item in Assets)
        {
            if (item.Key.Decorated != key.Decorated || item.Key.Primary != key.Primary) continue;
            sameStyle++;
            float distance = MathF.Abs(MathF.Log((float)item.Key.Width / key.Width))
                + MathF.Abs(MathF.Log((float)item.Key.Height / key.Height));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            nearest = item.Value;
        }
        // Never evict sprites still referenced by pooled Images. At the style limit, use the nearest size
        // rather than repeatedly baking during resize. Four styles * 20 entries * 65536 pixels is bounded.
        if (sameStyle >= MaximumVariantsPerStyle || _uploadUnavailable || Failed.Contains(key)) return nearest?.Sprite;

        Texture2D? texture = null;
        Sprite? sprite = null;
        try
        {
            Color[] pixels = Paint(key);
            texture = new Texture2D(key.Width, key.Height, TextureFormat.RGBA32, false)
            {
                name = $"RukariOfficialButton_{key.Width}x{key.Height}_{key.Decorated}_{key.Primary}",
                filterMode = FilterMode.Bilinear,
                // These sprites use the whole texture. Bilinear filtering at a fractional screen edge must
                // keep sampling that edge, rather than wrap the opaque top row into the fading bottom shadow.
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(pixels);
            texture.Apply();
            sprite = Sprite.Create(texture, new Rect(0, 0, key.Width, key.Height), new Vector2(.5f, .5f),
                100f, 0, SpriteMeshType.FullRect, Vector4.zero);
            Assets[key] = new Asset(texture, sprite);
            return sprite;
        }
        catch (Exception ex)
        {
            if (sprite is not null) Object.Destroy(sprite);
            if (texture is not null) Object.Destroy(texture);
            Failed.Add(key);
            // All variants share the same upload API. Do not retry a broken API at every new size.
            _uploadUnavailable = true;
            _log?.Invoke($"Official button baking unavailable; using the shared theme: {ex.GetType().Name}: {ex.Message}");
            return nearest?.Sprite;
        }
    }

    private static Pixels? Read(string spriteRef, Vector4 fallbackBorder)
    {
        if (Sources.TryGetValue(spriteRef, out Pixels? cached)) return cached;
        Pixels? result = null;
        try
        {
            Sprite? sprite = AtlasEmblemSource.GetSprite(spriteRef);
            if (sprite is not null)
            {
                Texture2D texture = sprite.texture;
                int width = texture.width;
                int height = texture.height;
                // AtlasEmblemSource returns a readable crop; never read all four million atlas pixels here.
                if (width > 0 && height > 0 && (long)width * height <= 262144)
                {
                    Color[] data = texture.GetPixels().ToArray();
                    Vector4 border = sprite.border;
                    if (border.x + border.y + border.z + border.w <= 0f) border = fallbackBorder;
                    if (data.Length == width * height) result = new Pixels(width, height, data, border);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Official button source '{spriteRef}' cannot be sampled; using the procedural plate: {ex.GetType().Name}: {ex.Message}");
        }
        Sources[spriteRef] = result; // Cache absence/failure as well.
        return result;
    }

    private static Color[] Paint(AssetKey key)
    {
        Pixels? plate = key.Decorated
            ? Read(LargePlate, new Vector4(30, 35, 30, 30))
            : Read(SmallPlate, new Vector4(20, 30, 20, 15));
        Pixels? facet = key.Decorated ? Read(key.Primary ? YellowFacet : BlueFacet, Vector4.zero) : null;
        Color fill = !key.Decorated ? Color.white
            : key.Primary ? new Color(1f, .925f, .25f, 1f) : new Color(.86f, .94f, .975f, 1f);
        var output = new Color[key.Width * key.Height];
        float skew = Math.Min(key.Width * .20f, key.Height * Shear);
        float faceWidth = key.Width - skew;
        // Settings small buttons are 100 design units high. Scale the artwork borders with the control so
        // short rows retain small corners/light shadows instead of becoming capsules.
        float artworkScale = key.Height / 100f;
        float faceBottom = key.Height * .14f;
        float facetHeight = key.Height - faceBottom;
        // Keep the source aspect even on narrow actions: the button face clips the artwork, rather than
        // squeezing triangles horizontally. The two softly fading ends may overlap on very narrow buttons.
        float facetWidth = facet is null ? 0f : facetHeight * facet.Width / facet.Height;

        for (int y = 0; y < key.Height; y++)
        for (int x = 0; x < key.Width; x++)
        {
            float py = y + .5f;
            float px = x + .5f;
            float flatX = px - skew * py / key.Height;
            Color sample = plate is null
                ? Procedural(flatX, py, faceWidth, key.Height)
                : SampleSliced(plate, flatX, py, faceWidth, key.Height, artworkScale);
            float brightness = (sample.r + sample.g + sample.b) / 3f;
            float faceCover = Math.Clamp((brightness - .30f) / .55f, 0f, 1f) * sample.a;
            Color color = new(sample.r * fill.r, sample.g * fill.g, sample.b * fill.b, sample.a);
            // Dense neutral controls need a quieter shadow. Preserve the face/edge alpha and attenuate only
            // the dark plate pixels; decorated bottom actions keep the original recipe byte for byte.
            if (!key.Decorated) color.a *= .5f + .5f * Math.Clamp((brightness - .30f) / .55f, 0f, 1f);
            if (facet is not null && facetWidth > 0f && py >= faceBottom && faceCover > 0f)
            {
                float v = (py - faceBottom) / facetHeight;
                // Right uses recorded orientation; left mirrors both axes, as the native hierarchy does.
                // Facet art already has a diagonal edge, so sample it in output space and clip to the face.
                if (px >= key.Width - facetWidth)
                    color = BlendFacet(color, Sample(facet, (px - key.Width + facetWidth) / facetWidth, v), faceCover);
                if (px <= facetWidth)
                    color = BlendFacet(color, Sample(facet, 1f - px / facetWidth, 1f - v), faceCover);
            }
            output[y * key.Width + x] = color;
        }
        return output;
    }

    private static Color SampleSliced(Pixels source, float x, float y, float width, float height, float scale)
    {
        if (x < 0f || x >= width || y < 0f || y >= height) return new Color(0, 0, 0, 0);
        float sx = Slice(x, width, source.Width, source.Border.x, source.Border.z, scale);
        float sy = Slice(y, height, source.Height, source.Border.y, source.Border.w, scale);
        return Sample(source, sx / source.Width, sy / source.Height);
    }

    private static float Slice(float p, float target, int source, float first, float last, float scale)
    {
        float a = Math.Min(first * scale, target * .45f);
        float b = Math.Min(last * scale, target * .45f);
        if (a > 0f && p < a) return p / a * first;
        if (b > 0f && p > target - b) return source - (target - p) / b * last;
        return first + (p - a) / Math.Max(.001f, target - a - b) * Math.Max(0f, source - first - last);
    }

    private static Color Sample(Pixels source, float u, float v)
    {
        if (u < 0f || u > 1f || v < 0f || v > 1f) return new Color(0, 0, 0, 0);
        float x = Math.Clamp(u * source.Width - .5f, 0f, source.Width - 1f);
        float y = Math.Clamp(v * source.Height - .5f, 0f, source.Height - 1f);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, source.Width - 1), y1 = Math.Min(y0 + 1, source.Height - 1);
        return Mix(Mix(source.Data[y0 * source.Width + x0], source.Data[y0 * source.Width + x1], x - x0),
            Mix(source.Data[y1 * source.Width + x0], source.Data[y1 * source.Width + x1], x - x0), y - y0);
    }

    private static Color Mix(Color a, Color b, float t) => new(a.r + (b.r - a.r) * t,
        a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);

    private static Color BlendFacet(Color plate, Color facet, float faceCover)
    {
        float alpha = facet.a * faceCover;
        return new Color(plate.r + (facet.r - plate.r) * alpha, plate.g + (facet.g - plate.g) * alpha,
            plate.b + (facet.b - plate.b) * alpha, plate.a);
    }

    private static Color Procedural(float x, float y, float width, float height)
    {
        float inset = Math.Max(.5f, height * .012f);
        float bottom = height * .14f;
        float radius = Math.Max(1f, height * .075f);
        float face = RoundedCoverage(x, y, inset, bottom, width - inset, height - inset, radius);
        float shadow = RoundedCoverage(x, y, inset, height * .035f, width - inset, height - bottom, radius + .5f) * .30f;
        float alpha = face + shadow * (1f - face);
        if (alpha <= 0f) return new Color(0, 0, 0, 0);
        float light = (face + .13f * shadow * (1f - face)) / alpha;
        return new Color(light, light, light, alpha);
    }

    private static float RoundedCoverage(float x, float y, float left, float bottom, float right, float top, float radius)
    {
        if (right <= left || top <= bottom) return 0f;
        radius = Math.Min(radius, Math.Min(right - left, top - bottom) * .5f);
        float cx = Math.Clamp(x, left + radius, right - radius);
        float cy = Math.Clamp(y, bottom + radius, top - radius);
        float dx = x - cx, dy = y - cy;
        return Math.Clamp(radius + .5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
    }

    internal static void Shutdown()
    {
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return;
        foreach (Asset asset in Assets.Values)
        {
            Object.Destroy(asset.Sprite);
            Object.Destroy(asset.Texture);
        }
        Assets.Clear();
        Sources.Clear();
        Failed.Clear();
        _uploadUnavailable = false;
        _threadId = 0;
        _log = null;
    }
}
