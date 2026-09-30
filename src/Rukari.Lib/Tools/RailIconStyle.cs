using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rukari.Lib.Tools;

/// <summary>
/// Pure managed styling for the permanent rail: which built-in glyph a module asks for, and the stable accent
/// colour that keeps two mods visually distinct even when neither ships an icon. No artwork is referenced, so
/// this stays inside the portable API assembly and needs no game types.
/// </summary>
public static class RailIconStyle
{
    /// <summary>Glyph used when a module names nothing, or names a glyph we do not draw.</summary>
    public const string DefaultGlyph = "gear";

    /// <summary>Every glyph the runtime painter can draw.</summary>
    public static readonly IReadOnlyList<string> Glyphs = new[] { "wave", "camera", "card", "magnify", "gear", "master" };

    /// <summary>
    /// Maps a module's requested icon id onto a drawable glyph, never returning an empty value.
    ///
    /// The rail's own expand/collapse icon is the module whose id is <see cref="MasterModuleId"/>; it must map
    /// to <c>master</c> rather than falling through to the default, because the atlas carries a distinct emblem
    /// for it (the pink phone the user picked) and falling through is what showed a gear instead.
    /// </summary>
    public static string GlyphFor(string? iconId) => (iconId ?? "").Trim().ToLowerInvariant() switch
    {
        "" => DefaultGlyph,
        MasterModuleId or "phone" or "momotal" => "master",
        "voice" or "sound" or "audio" or "voice-wave" or "wave" => "wave",
        "effects" or "effect" or "camera" or "visual" or "motion" or "picture" => "camera",
        "lobby" or "memory" or "spine" or "card" or "hall" => "card",
        "search" or "magnify" or "find" => "magnify",
        _ => DefaultGlyph
    };

    /// <summary>Icon id of the permanent rail's own expand/collapse module.</summary>
    public const string MasterModuleId = "master";

    /// <summary>Stable accent colour for a module id; the same id always yields the same colour.</summary>
    public static ToolColor AccentFor(string? moduleId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(moduleId ?? ""));
        int hue = (hash[0] << 8 | hash[1]) % 360;
        return FromHsl(hue, .52f, .46f);
    }

    /// <summary>Accent colour as an uppercase RRGGBB key, so identical colours share one painted texture.</summary>
    public static string AccentKey(string? moduleId)
    {
        ToolColor c = AccentFor(moduleId);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)MathF.Round(c.R * 255):X2}{(int)MathF.Round(c.G * 255):X2}{(int)MathF.Round(c.B * 255):X2}");
    }

    /// <summary>Relative luminance, used to pick a readable glyph colour over the accent.</summary>
    public static double Luminance(ToolColor c) => .2126 * c.R + .7152 * c.G + .0722 * c.B;

    private static ToolColor FromHsl(int hue, float saturation, float lightness)
    {
        float c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        float h = hue / 60f;
        float x = c * (1 - Math.Abs(h % 2 - 1));
        (float r, float g, float b) = (int)h switch
        {
            0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x),
            3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x)
        };
        float m = lightness - c / 2;
        return new ToolColor(r + m, g + m, b + m);
    }
}
