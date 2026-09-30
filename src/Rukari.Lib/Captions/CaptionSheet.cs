using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rukari.Lib.Captions;

/// <summary>
/// One caption: the text, when it appears, and how it is styled. A caption is either placed on the music's own
/// timeline (<see cref="At"/>) or bound to a scenario line (<see cref="Line"/>), and the same type serves both —
/// which is why the layer has two sources but only one renderer.
/// </summary>
public sealed record CaptionCue
{
    /// <summary>Stable identity, used as the seed of this line's motion. Defaults to its own text.</summary>
    public string? Id { get; init; }

    /// <summary>The text to show. One entry per character is drawn independently, so this is the real string.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Seconds into the track this caption starts at. Ignored by a line-bound caption that has no clock.</summary>
    public double At { get; init; }

    /// <summary>
    /// Explicit end, in the same seconds as <see cref="At"/>. A caption imported from a subtitle file carries one;
    /// a hand-written caption leaves it out and gets <see cref="CaptionTimeline"/>'s own reveal + hold instead.
    /// </summary>
    public double? End { get; init; }

    /// <summary>Seconds the finished line stays fully visible before it starts to fade.</summary>
    public double Hold { get; init; } = 1.6;

    /// <summary>
    /// Which style key this caption speaks with: a character id such as <c>CH0335</c>, or <c>lyric</c>. This is
    /// what makes a colour follow a speaker instead of being typed into every line.
    /// </summary>
    public string? Speaker { get; init; }

    /// <summary>Scenario line this caption belongs to, for the dialogue-bound mode.</summary>
    public string? Line { get; init; }

    /// <summary>Draw this caption larger, with its characters emphasised. For the one line that must land.</summary>
    public bool Emphasis { get; init; }

    /// <summary>
    /// Indices of the characters that carry the emphasis this line is built around. Empty means the whole line is
    /// emphasised when <see cref="Emphasis"/> is set.
    /// </summary>
    public IReadOnlyList<int>? EmphasisCharacters { get; init; }

    /// <summary>Per-line style, layered on top of the speaker and the sheet default.</summary>
    public CaptionStyle? Style { get; init; }

    /// <summary>
    /// Per-character appearance times in seconds from <see cref="At"/>, as an enhanced LRC file provides. When it
    /// matches the text length every character keeps its own beat, which is what makes a caption follow a song.
    /// </summary>
    public IReadOnlyList<double>? CharacterTimes { get; init; }

    /// <summary>Screen x of the caption's centre, 0..1, when the author pinned this line by hand.</summary>
    public double? AnchorX { get; init; }

    /// <summary>Screen y of the caption's centre, 0..1, where 1 is the top edge.</summary>
    public double? AnchorY { get; init; }

    /// <summary>Identity used for the motion seed: the id when there is one, otherwise the text itself.</summary>
    public string SeedKey => string.IsNullOrWhiteSpace(Id) ? Text : Id!;
}

/// <summary>
/// One caption sheet: the file the editor writes and the layer reads. It holds both the styles and the captions the
/// same way, so a song's lyrics and a boss's lines are the same kind of data.
/// </summary>
public sealed record CaptionSheet
{
    /// <summary>Sheet format version, so an older reader can refuse a newer file instead of guessing.</summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// <c>loop</c> plays the sheet against a track on its own clock and repeats it; <c>script</c> shows each caption
    /// when the scenario line it names is on screen.
    /// </summary>
    public string Mode { get; init; } = ModeLoop;

    /// <summary>The BGM this sheet follows, when it plays on the track's clock.</summary>
    public string? Track { get; init; }

    /// <summary>Global nudge in seconds, positive meaning the captions appear later than the file says.</summary>
    public double OffsetSeconds { get; init; }

    /// <summary>Whether the sheet starts over when the track does.</summary>
    public bool Loop { get; init; } = true;

    /// <summary>The sheet's own defaults, the lowest layer of every caption's style.</summary>
    public CaptionStyle? Default { get; init; }

    /// <summary>One style per speaker, keyed by character id or by <c>lyric</c>.</summary>
    public Dictionary<string, CaptionStyle> Speakers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The captions themselves.</summary>
    public List<CaptionCue> Lines { get; init; } = new();

    /// <summary>The sheet mode that plays against a track's clock and repeats.</summary>
    public const string ModeLoop = "loop";

    /// <summary>The sheet mode that shows a caption while the scenario line it names is on screen.</summary>
    public const string ModeScript = "script";

    /// <summary>True when this sheet is driven by a track's clock rather than by scenario lines.</summary>
    [JsonIgnore]
    public bool IsTrackDriven => !string.Equals(Mode, ModeScript, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Reads a sheet from JSON. Throws <see cref="JsonException"/> on a malformed file.</summary>
    public static CaptionSheet Load(string json) =>
        JsonSerializer.Deserialize<CaptionSheet>(json, Options) ?? new CaptionSheet();

    /// <summary>Reads a sheet from disk, returning false instead of throwing when the file is missing or broken.</summary>
    public static bool TryLoadFile(string path, out CaptionSheet sheet, out string error)
    {
        sheet = new CaptionSheet();
        error = string.Empty;
        try
        {
            if (!File.Exists(path)) { error = "The caption sheet does not exist."; return false; }
            sheet = Load(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Writes this sheet as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// The style one caption is drawn with: the sheet default, then its speaker, then the line itself. This is the
    /// whole reason a colour can follow a character while the size stays global.
    /// </summary>
    public ResolvedCaptionStyle StyleFor(CaptionCue cue) => StyleFor(cue, null);

    /// <summary>
    /// The same resolution with one more layer on top, which is what a live editor needs: the page changes a size
    /// or a colour and every caption on screen picks it up without the sheet on disk being rewritten.
    /// </summary>
    public ResolvedCaptionStyle StyleFor(CaptionCue cue, CaptionStyle? extra)
    {
        CaptionStyle style = Default ?? CaptionStyle.Empty;
        if (cue.Speaker is { Length: > 0 } speaker && Speakers.TryGetValue(speaker, out CaptionStyle? speakerStyle))
            style = style.Over(speakerStyle);
        return style.Over(cue.Style).Over(extra).Resolve();
    }

    /// <summary>The captions in the order they play, which is what both modes walk.</summary>
    public IReadOnlyList<CaptionCue> Ordered() =>
        Lines.OrderBy(cue => cue.At).ToList();

    /// <summary>
    /// The caption that should be on screen at <paramref name="seconds"/> of the track, or null while none is. A
    /// caption owns the window from its own start to the end of its reveal, hold and fade, so two captions may
    /// deliberately overlap in a file; the later one wins.
    /// </summary>
    public CaptionCue? CueAt(double seconds, CaptionMotionRules rules)
    {
        CaptionCue? found = null;
        foreach (CaptionCue cue in Lines)
        {
            double start = cue.At + OffsetSeconds;
            if (seconds < start) continue;
            if (seconds > start + CaptionTimeline.Duration(cue, rules)) continue;
            if (found is null || cue.At >= found.At) found = cue;
        }
        return found;
    }

    /// <summary>The caption bound to a scenario line, or null when that line carries none.</summary>
    public CaptionCue? CueForLine(string? lineId)
    {
        if (string.IsNullOrWhiteSpace(lineId)) return null;
        foreach (CaptionCue cue in Lines)
            if (string.Equals(cue.Line, lineId, StringComparison.Ordinal)) return cue;
        return null;
    }
}
