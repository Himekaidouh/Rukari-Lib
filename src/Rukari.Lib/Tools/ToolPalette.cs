namespace Rukari.Lib.Tools;

/// <summary>A portable, opaque sRGB color used by the tool views.</summary>
public readonly record struct ToolColor(float R, float G, float B);

/// <summary>Background and text colors that must be applied together.</summary>
public readonly record struct ToolColorPair(ToolColor Background, ToolColor Foreground);

/// <summary>Readable light surfaces shared by native tool views; contains no Unity objects.</summary>
public static class ToolPalette
{
    /// <summary>Default dark blue text on light surfaces.</summary>
    public static readonly ToolColor Text = new(.12f, .20f, .29f);
    /// <summary>The opaque drawer surface.</summary>
    public static readonly ToolColorPair Panel = new(new(.97f, .98f, .99f), Text);
    /// <summary>A quiet header or value display.</summary>
    public static readonly ToolColorPair Header = new(new(.86f, .91f, .96f), Text);
    /// <summary>A normal available button.</summary>
    public static readonly ToolColorPair Normal = new(new(.89f, .93f, .97f), Text);
    /// <summary>A selected page, slot, or mode.</summary>
    public static readonly ToolColorPair Selected = new(new(.68f, .84f, .97f), Text);
    /// <summary>An unavailable button, still legible without looking active.</summary>
    public static readonly ToolColorPair Disabled = new(new(.87f, .89f, .91f), new(.36f, .40f, .45f));
    /// <summary>The main commit action and the shared toolbox entrance.</summary>
    public static readonly ToolColorPair Primary = new(new(.10f, .35f, .61f), new(1f, 1f, 1f));
    /// <summary>A reversible secondary action such as undo.</summary>
    public static readonly ToolColorPair Undo = new(new(.97f, .86f, .64f), Text);
    /// <summary>
    /// The module card: the surface the module rows live in, and the first level of the left drawer. Its colours are
    /// the editor's own — the panel grey and the dark navy it titles everything with — so the card reads as part of
    /// the game rather than as a mod's window.
    /// </summary>
    public static readonly ToolColorPair Card = new(new(.937f, .937f, .937f), new(.188f, .282f, .376f));
    /// <summary>The card's title band, one step darker than the card, like the editor's own row plate.</summary>
    public static readonly ToolColorPair CardHeader = new(new(.890f, .906f, .906f), new(.188f, .282f, .376f));
    /// <summary>The module column's scrollbar track. It carries no text of its own.</summary>
    public static readonly ToolColorPair ScrollTrack = new(new(.867f, .890f, .902f), Text);
    /// <summary>The scrollbar thumb, in the editor's primary blue: the one strong accent in the drawer.</summary>
    public static readonly ToolColorPair ScrollThumb = new(new(.490f, .694f, .812f), new(1f, 1f, 1f));

    /// <summary>Gets a complete surface pair for a supported rendering key.</summary>
    public static ToolColorPair ForKey(string key) => key switch
    {
        "panel" => Panel,
        "header" => Header,
        "button.disabled" => Disabled,
        "button.selected" => Selected,
        "button.primary" => Primary,
        "button.undo" => Undo,
        "panel.card" => Card,
        "panel.cardHeader" => CardHeader,
        "scroll.track" => ScrollTrack,
        "scroll.thumb" => ScrollThumb,
        _ => Normal
    };
}
