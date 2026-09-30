using System.Globalization;

namespace Rukari.Lib.Captions;

/// <summary>A colour with alpha, because a caption's outline is usually drawn semi-transparent.</summary>
public readonly record struct CaptionColor(float R, float G, float B, float A = 1f)
{
    /// <summary>Opaque white, the colour a caption falls back to.</summary>
    public static readonly CaptionColor White = new(1f, 1f, 1f);

    /// <summary>Opaque black, the outline a caption falls back to.</summary>
    public static readonly CaptionColor Black = new(0f, 0f, 0f);

    /// <summary>
    /// Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c>, with or without the hash. Anything else yields the fallback, so a
    /// hand-written style sheet can never take the caption layer down with a typo.
    /// </summary>
    public static CaptionColor Parse(string? value, CaptionColor fallback)
    {
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        if (text.Length is not (6 or 8)) return fallback;
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed)) return fallback;
        return text.Length == 6
            ? new CaptionColor(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f)
            : new CaptionColor(
                ((packed >> 24) & 0xFF) / 255f,
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f);
    }

    /// <summary>The same colour as <c>#RRGGBBAA</c>, which is what the style sheet stores.</summary>
    public string ToHex() => $"#{Channel(R)}{Channel(G)}{Channel(B)}{Channel(A)}";

    private static string Channel(float value) =>
        ((int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f)).ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>Multiplies the alpha, which is how the layer dims a whole line without touching its colours.</summary>
    public CaptionColor WithAlpha(float alpha) => this with { A = Math.Clamp(A * alpha, 0f, 1f) };
}

/// <summary>
/// How one caption is drawn. Every field is optional so the three levels can be layered: the sheet's default, then
/// the speaker, then the single line. A missing field means "ask the level above", which is what lets a colour
/// follow a character while the size stays global.
/// </summary>
public sealed record CaptionStyle
{
    /// <summary>Font size in physical pixels at the screen the caption layer measures against.</summary>
    public int? FontSize { get; init; }

    /// <summary>Fill colour, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public string? Color { get; init; }

    /// <summary>Outline colour. The outline is what keeps the text readable over a bright CG.</summary>
    public string? OutlineColor { get; init; }

    /// <summary>Outline width in physical pixels.</summary>
    public float? OutlineWidth { get; init; }

    /// <summary>Draw the text bold.</summary>
    public bool? Bold { get; init; }

    /// <summary>
    /// How heavy the strokes are drawn, 0 (as the font ships) to 3 (noticeably heavier). A localisation font often
    /// ships in ONE weight, so a heavier caption has to be faked by drawing the glyph slightly enlarged in every
    /// direction; this is the switch for that.
    /// </summary>
    public int? Weight { get; init; }

    /// <summary>How much an emphasised character grows, as a fraction. 1.1 means ten percent larger.</summary>
    public float? EmphasisScale { get; init; }

    /// <summary>How many lines a caption may wrap to before the layer shrinks the font instead.</summary>
    public int? MaximumLines { get; init; }

    /// <summary>An empty style, which asks every level above for its value.</summary>
    public static CaptionStyle Empty { get; } = new();

    /// <summary>This style with <paramref name="over"/> applied on top of it; only the fields it sets are replaced.</summary>
    public CaptionStyle Over(CaptionStyle? over) => over is null ? this : new CaptionStyle
    {
        FontSize = over.FontSize ?? FontSize,
        Color = over.Color ?? Color,
        OutlineColor = over.OutlineColor ?? OutlineColor,
        OutlineWidth = over.OutlineWidth ?? OutlineWidth,
        Bold = over.Bold ?? Bold,
        Weight = over.Weight ?? Weight,
        EmphasisScale = over.EmphasisScale ?? EmphasisScale,
        MaximumLines = over.MaximumLines ?? MaximumLines
    };

    /// <summary>Fills in every unset field, so the renderer only ever sees numbers it can draw with.</summary>
    public ResolvedCaptionStyle Resolve() => new(
        Math.Clamp(FontSize ?? 52, 8, 400),
        CaptionColor.Parse(Color, CaptionColor.White),
        CaptionColor.Parse(OutlineColor, CaptionColor.Black),
        Math.Clamp(OutlineWidth ?? 3f, 0f, 24f),
        Bold ?? false,
        Math.Clamp(Weight ?? 0, 0, 3),
        Math.Clamp(EmphasisScale ?? 1.1f, 1f, 2f),
        Math.Clamp(MaximumLines ?? 2, 1, 6));
}

/// <summary>A caption style with every optional field decided.</summary>
public readonly record struct ResolvedCaptionStyle(
    int FontSize,
    CaptionColor Color,
    CaptionColor OutlineColor,
    float OutlineWidth,
    bool Bold,
    int Weight,
    float EmphasisScale,
    int MaximumLines);
