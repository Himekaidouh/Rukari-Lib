extern alias unitycore;

using Rukari.Lib.Tools;
using Graphics = unitycore::UnityEngine.Graphics;
using RenderTexture = unitycore::UnityEngine.RenderTexture;
using FilterMode = unitycore::UnityEngine.FilterMode;
using Object = unitycore::UnityEngine.Object;
using Rect = unitycore::UnityEngine.Rect;
using Sprite = unitycore::UnityEngine.Sprite;
using SpriteMeshType = unitycore::UnityEngine.SpriteMeshType;
using Texture2D = unitycore::UnityEngine.Texture2D;
using TextureFormat = unitycore::UnityEngine.TextureFormat;
using Vector2 = unitycore::UnityEngine.Vector2;
using Vector4 = unitycore::UnityEngine.Vector4;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// Shared skin loaded only from the ui directory beside Rukari.Lib.Runtime.dll. The runtime package owns
/// the atlas, matching metadata and popup decorations; feature mods do not carry another copy. A missing or
/// damaged set falls back to procedural painting instead of reading a different installation's artwork.
///
/// Rectangles come from the bundled metadata verified with its PNG. The tables below record glyph IDs and
/// legacy region defaults; a missing metadata file rejects the entire skin before any texture is loaded.
///
/// An emblem has no border and is drawn as Image.Type.Simple; a plate carries the atlas's own nine-slice border
/// and is drawn as Image.Type.Sliced, which is what lets an official button stretch to any width.
/// </summary>
public static class AtlasEmblemSource
{
    /// <summary>The atlas every emblem and plate id in the fallback tables belongs to.</summary>
    private const string CommonAtlas = "Common";

    /// <summary>Separates the atlas name from the sprite name in a public sprite reference.</summary>
    private const char SpriteRefSeparator = ':';

    /// <summary>One verified atlas region: sprite name, top-left-origin rect, tintability and slice border.</summary>
    internal readonly record struct Region(string Name, int X, int Y, int Width, int Height, bool Tintable, int Border = 0);

    private static readonly Dictionary<string, Region> Regions = new(StringComparer.Ordinal)
    {
        // The game's own character-voice icon: head with a headset and sound arcs, pure white.
        ["voice"] = new("Common_Icon_CV", 678, 1340, 94, 75, true),
        // Closest thing to a camera in the whole atlas: a stack of photos with a sun and a mountain.
        ["effects"] = new("Cafe_Icon_Photo", 1425, 969, 120, 104, true),
        // A filmstrip with a play triangle; reads very solid at rail size.
        ["effects-alt"] = new("Cafe_Icon_Video", 295, 698, 120, 84, true),
        // Literally named Memory Lobby: a pink card with a person silhouette. Coloured, so not tintable.
        ["lobby"] = new("Common_Icon_MemoryLobby", 948, 372, 55, 43, false),
        // A white stack of ID cards; perfectly square and tintable.
        ["lobby-alt"] = new("Common_Icon_IDCard_Small", 334, 566, 67, 65, true),
        ["search"] = new("Common_Icon_Search", 1289, 502, 87, 88, true),
        // The most recognisable single glyph in the atlas: a crisp white gear, perfectly square.
        ["gear"] = new("Common_Icon_Option", 1875, 394, 80, 80, true),
        // The big icon the user picked: Arona pulling the gacha, the most recognisable large emblem in the game.
        ["master"] = new("Common_Icon_Gacha", 1189, 1744, 127, 163, false),
        // Previous choice, kept so a config or a future mod can still ask for the pink phone with a chat bubble.
        ["phone"] = new("Common_Icon_MoMoTalk", 1504, 1074, 119, 159, false)
    };

    /// <summary>
    /// Stretchable official plates. Each carries the atlas's own nine-slice border, so one sprite draws a correct
    /// button at any size instead of being squashed into a fixed picture.
    ///
    /// The row plate is the game's rounded background (radius 10 of 24x24) rather than its square button
    /// (Common_Sub_Btn, 67x71 with border 25). That one is nearly square, so stretching it into a wide, short
    /// button turns its 25 pixel corner into a semicircle and the control reads as a dark navy pill; its fill is
    /// also dark enough that the shared dark text disappears on it. A white rounded background covers normal,
    /// selected and disabled by tint alone, so the label colour stays dark in every state. Both claims were
    /// checked by rendering the slices offline (artifacts/diagnostics/New-ButtonMock.ps1) before choosing.
    ///
    /// The official *button* plate (Common_Btn_Normal_*_Pt) deliberately does not live here: it is 251x140 with an
    /// empty border and a diagonal cut baked into its pixels, so it must be scaled whole, never sliced. Callers ask
    /// for it by name through <see cref="GetSprite"/>.
    /// </summary>
    private static readonly Dictionary<string, Region> Plates = new(StringComparer.Ordinal)
    {
        ["row"] = new("Common_Bg_Raius10px", 1438, 461, 24, 24, true, 10)
    };

    /// <summary>A region with its atlas resolved, ready to be copied out of a loaded atlas texture.</summary>
    private readonly record struct ResolvedRegion(
        string Atlas,
        string Name,
        int X,
        int Y,
        int Width,
        int Height,
        int BorderLeft,
        int BorderTop,
        int BorderRight,
        int BorderBottom);

    /// <summary>One loaded atlas: its texture, the regions its export records, and where it came from.</summary>
    private sealed class AtlasHandle
    {
        internal AtlasHandle(Texture2D? texture, IReadOnlyDictionary<string, AtlasSpriteRegion> catalog)
        {
            Texture = texture;
            Catalog = catalog;
        }

        internal Texture2D? Texture { get; }

        internal IReadOnlyDictionary<string, AtlasSpriteRegion> Catalog { get; }
    }

    private static readonly Dictionary<string, AtlasHandle> Atlases = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Sprite> Loaded = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Sprite> Decorations = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    private static int _threadId;
    private static Action<string>? _log;
    private static BundledUiAssets? _assets;

    internal static void Initialize(Action<string> log)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _log = log;
        _assets = BundledUiAssets.Load(typeof(AtlasEmblemSource).Assembly.Location, out string? error);
        log(_assets is null ? $"Shared UI skin unavailable; using procedural art: {error}"
            : $"Shared UI skin verified beside Rukari.Lib.Runtime.dll ({_assets.Catalog.Count} sprite records).");
    }

    /// <summary>
    /// Returns an emblem for the glyph, or null when the atlas export is unavailable or the glyph has no
    /// verified region. Callers must fall back to the procedural painter on null. The art keeps its own
    /// colours, so callers must draw it untinted.
    /// </summary>
    public static Sprite? Get(string? glyphId) => Resolve("emblem", glyphId, Regions, sliced: false);

    /// <summary>
    /// Returns a stretchable official button plate, or null when the atlas export is unavailable. Callers must
    /// draw it with <c>Image.Type.Sliced</c> and fall back to the shared theme when it is null.
    /// </summary>
    public static Sprite? GetPlate(string? plateId) => Resolve("plate", plateId, Plates, sliced: true);

    /// <summary>Whether a plate id exists, so a caller can choose a layout without touching the atlas.</summary>
    public static bool HasPlate(string? plateId) => plateId is not null && Plates.ContainsKey(plateId.Trim().ToLowerInvariant());

    /// <summary>
    /// Returns any sprite the export records, addressed as <c>"&lt;Atlas&gt;:&lt;SpriteName&gt;"</c> — for example
    /// <c>"Common:Common_Btn_Normal_Y_S_Pt"</c>. The shared skin currently bundles Common only. A reference without an atlas prefix means
    /// <see cref="CommonAtlas"/>. Coordinates and borders come from the export's metadata, falling back to the
    /// hand-verified table only for an unprefixed name that table knows.
    /// </summary>
    /// <param name="spriteRef">Atlas-qualified sprite name.</param>
    /// <param name="sliced">
    /// True to keep the recorded nine-slice border, which is what a stretchable panel needs. Pass false for art
    /// whose diagonal edges are baked into its pixels, such as the official 251x140 button plates, because slicing
    /// those would stretch the cut edge and destroy the shape.
    /// </param>
    public static Sprite? GetSprite(string? spriteRef, bool sliced = true)
    {
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return null;
        if (!TrySplitRef(spriteRef, out string atlasName, out string spriteName)) return null;
        string key = atlasName + SpriteRefSeparator + spriteName;
        if (Missing.Contains(key)) return null;
        AtlasHandle handle = ResolveAtlas(atlasName);
        if (handle.Texture is null) return null;
        if (handle.Catalog.TryGetValue(spriteName, out AtlasSpriteRegion meta))
        {
            return Create("sprite", key, new ResolvedRegion(atlasName, meta.Name, meta.X, meta.Y, meta.Width,
                meta.Height, meta.BorderLeft, meta.BorderTop, meta.BorderRight, meta.BorderBottom), sliced);
        }

        if (string.Equals(atlasName, CommonAtlas, StringComparison.Ordinal) && TryFindVerified(spriteName, out Region region))
        {
            return Create("sprite", key, new ResolvedRegion(CommonAtlas, region.Name, region.X, region.Y, region.Width,
                region.Height, region.Border, region.Border, region.Border, region.Border), sliced);
        }

        Missing.Add(key);
        _log?.Invoke($"Atlas sprite '{key}' is not recorded in the export; the caller keeps its procedural art.");
        return null;
    }

    /// <summary>
    /// Returns one of the export's standalone decoration textures (the <c>decorations\</c> folder), which are not
    /// part of any atlas and carry no border. Null when the export is unavailable; callers then draw no decoration
    /// and keep whatever they had.
    /// </summary>
    /// <param name="fileName">File name without extension, for example <c>Popup_Img_Deco_2</c>.</param>
    public static Sprite? GetDecoration(string? fileName)
    {
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return null;
        string name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        if (Decorations.TryGetValue(name, out Sprite? cached)) return cached;
        string key = "deco:" + name;
        if (Missing.Contains(key)) return null;
        byte[]? bytes = _assets?.Decoration(name);
        if (bytes is not null)
        {
            Texture2D? texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    name = "RukariDecoration_" + name
                };
                if (!PngDecoder.TryLoad(texture, bytes))
                {
                    Object.Destroy(texture);
                    texture = null;
                    throw new InvalidDataException("Bundled decoration PNG could not be decoded.");
                }

                var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect, Vector4.zero);
                sprite.name = name;
                texture = null;
                Decorations[name] = sprite;
                _log?.Invoke($"Shared UI decoration '{name}' loaded {sprite.texture.width}x{sprite.texture.height}.");
                return sprite;
            }
            catch (Exception ex)
            {
                if (texture is not null) Object.Destroy(texture);
                _log?.Invoke($"Shared UI decoration '{name}' could not be read: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Missing.Add(key);
        _log?.Invoke($"Decoration '{name}' is unavailable; the caller keeps its procedural art.");
        return null;
    }

    private static bool TrySplitRef(string? spriteRef, out string atlasName, out string spriteName)
    {
        atlasName = CommonAtlas;
        spriteName = string.Empty;
        string text = (spriteRef ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        int separator = text.IndexOf(SpriteRefSeparator);
        if (separator < 0)
        {
            spriteName = text;
            return true;
        }

        atlasName = text.Substring(0, separator).Trim();
        spriteName = text.Substring(separator + 1).Trim();
        return atlasName.Length > 0 && spriteName.Length > 0;
    }

    /// <summary>Looks a sprite name up in the hand-verified tables, which are keyed by the rail's own glyph ids.</summary>
    private static bool TryFindVerified(string spriteName, out Region region)
    {
        foreach (Region candidate in Regions.Values)
        {
            if (!string.Equals(candidate.Name, spriteName, StringComparison.Ordinal)) continue;
            region = candidate;
            return true;
        }

        foreach (Region candidate in Plates.Values)
        {
            if (!string.Equals(candidate.Name, spriteName, StringComparison.Ordinal)) continue;
            region = candidate;
            return true;
        }

        region = default;
        return false;
    }

    private static Sprite? Resolve(string kind, string? id, Dictionary<string, Region> regions, bool sliced)
    {
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return null;
        string key = (id ?? "").Trim().ToLowerInvariant();
        if (!regions.TryGetValue(key, out Region region)) return null;

        // The export owns the coordinates: metadata wins over the hand-verified record whenever it names the sprite.
        AtlasHandle handle = ResolveAtlas(CommonAtlas);
        if (handle.Catalog.TryGetValue(region.Name, out AtlasSpriteRegion meta))
        {
            return Create(kind, CommonAtlas + SpriteRefSeparator + meta.Name,
                new ResolvedRegion(CommonAtlas, meta.Name, meta.X, meta.Y, meta.Width, meta.Height,
                    meta.BorderLeft, meta.BorderTop, meta.BorderRight, meta.BorderBottom), sliced);
        }

        return Create(kind, CommonAtlas + SpriteRefSeparator + region.Name,
            new ResolvedRegion(CommonAtlas, region.Name, region.X, region.Y, region.Width, region.Height,
                region.Border, region.Border, region.Border, region.Border), sliced);
    }

    private static Sprite? Create(string kind, string key, ResolvedRegion region, bool sliced)
    {
        if (_threadId == 0 || _threadId != Environment.CurrentManagedThreadId) return null;
        if (Missing.Contains(key)) return null;
        if (Loaded.TryGetValue(key, out Sprite? cached)) return cached;
        AtlasHandle handle = ResolveAtlas(region.Atlas);
        Texture2D? atlas = handle.Texture;
        if (atlas is null) return null;
        Texture2D? copy = null;
        try
        {
            copy = new Texture2D(region.Width, region.Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                name = "RukariEmblem_" + region.Name
            };
            // Read ONLY the source region. Copying the whole atlas would move 419万 pixels per emblem on the
            // main thread, which is what stalled project entry.
            RenderTexture? previous = RenderTexture.active;
            RenderTexture? scratch = RenderTexture.GetTemporary(atlas.width, atlas.height, 0);
            try
            {
                Graphics.Blit(atlas, scratch);
                RenderTexture.active = scratch;
                // The recorded rect uses a top-left origin; Unity textures are bottom-left.
                int sourceY = atlas.height - (region.Y + region.Height);
                copy.ReadPixels(new Rect(region.X, sourceY, region.Width, region.Height), 0, 0, false);
                copy.Apply();
            }
            finally
            {
                RenderTexture.active = previous;
                if (scratch is not null) RenderTexture.ReleaseTemporary(scratch);
            }

            // Unity's border vector is (left, bottom, right, top); the recorded order is (left, top, right, bottom).
            int left = sliced ? region.BorderLeft : 0;
            int top = sliced ? region.BorderTop : 0;
            int right = sliced ? region.BorderRight : 0;
            int bottom = sliced ? region.BorderBottom : 0;
            var sprite = Sprite.Create(copy, new Rect(0, 0, region.Width, region.Height), new Vector2(.5f, .5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(left, bottom, right, top));
            sprite.name = region.Name;
            copy = null;
            Loaded[key] = sprite;
            _log?.Invoke($"Atlas {kind} '{key}' -> {region.Width}x{region.Height} from ({region.X},{region.Y}) border={left},{top},{right},{bottom}.");
            return sprite;
        }
        catch (Exception ex)
        {
            if (copy is not null) Object.Destroy(copy);
            Missing.Add(key);
            _log?.Invoke($"Atlas {kind} '{key}' unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Loads a decoded atlas export once, together with the regions its metadata records.</summary>
    private static AtlasHandle ResolveAtlas(string atlasName)
    {
        if (Atlases.TryGetValue(atlasName, out AtlasHandle? cached)) return cached;
        AtlasHandle handle = LoadAtlas(atlasName);
        Atlases[atlasName] = handle;
        return handle;
    }

    private static AtlasHandle LoadAtlas(string atlasName)
    {
        byte[]? bytes = _assets?.Atlas(atlasName);
        if (bytes is not null)
        {
            Texture2D? texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    name = "RukariAtlas" + atlasName
                };
                if (!PngDecoder.TryLoad(texture, bytes))
                {
                    Object.Destroy(texture);
                    texture = null;
                    throw new InvalidDataException("Bundled atlas PNG could not be decoded.");
                }

                IReadOnlyDictionary<string, AtlasSpriteRegion> catalog = _assets!.Catalog;
                _log?.Invoke($"Shared UI atlas '{atlasName}' loaded ({texture.width}x{texture.height}, {catalog.Count} recorded regions).");
                return new AtlasHandle(texture, catalog);
            }
            catch (Exception ex)
            {
                if (texture is not null) Object.Destroy(texture);
                _log?.Invoke($"Shared UI atlas '{atlasName}' could not be read: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _log?.Invoke($"Shared UI atlas '{atlasName}' is unavailable; callers keep their procedural art.");
        return new AtlasHandle(null, AtlasSpriteCatalog.None);
    }

    internal static void Shutdown()
    {
        if (_threadId != Environment.CurrentManagedThreadId) return;
        DestroyAll(Loaded);
        DestroyAll(Decorations);
        foreach (AtlasHandle handle in Atlases.Values)
        {
            if (handle.Texture is not null) Object.Destroy(handle.Texture);
        }

        Atlases.Clear();
        Missing.Clear();
        _threadId = 0;
        _log = null;
        _assets = null;
    }

    private static void DestroyAll(Dictionary<string, Sprite> sprites)
    {
        foreach (Sprite sprite in sprites.Values)
        {
            if (sprite is null) continue;
            Object.Destroy(sprite.texture);
            Object.Destroy(sprite);
        }

        sprites.Clear();
    }
}
