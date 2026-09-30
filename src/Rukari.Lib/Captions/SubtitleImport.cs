using System.Globalization;
using System.Text;

namespace Rukari.Lib.Captions;

/// <summary>
/// Reads the subtitle formats a song or a film already comes with, so a caption sheet does not have to be typed by
/// hand. Standard LRC gives one time per line; enhanced LRC gives one time per character, which is exactly the beat
/// the typewriter and the per-character motion need.
/// </summary>
public static class SubtitleImport
{
    /// <summary>Reads a file by its extension, falling back to sniffing its content.</summary>
    public static IReadOnlyList<CaptionCue> FromFile(string path, string? speaker = "lyric")
    {
        string text = File.ReadAllText(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".lrc" => FromLrc(text, speaker),
            ".srt" => FromSrt(text, speaker),
            ".ass" or ".ssa" => FromAss(text, speaker),
            _ => LooksLikeSrt(text) ? FromSrt(text, speaker) : FromLrc(text, speaker)
        };
    }

    /// <summary>
    /// Parses LRC, both the plain <c>[mm:ss.xx]text</c> form and the enhanced <c>[mm:ss.xx]&lt;mm:ss.xx&gt;字…</c>
    /// form that carries a time for every character. Tags the format uses for metadata are read for the global
    /// <c>offset</c> and otherwise ignored, and one line may carry several timestamps.
    /// </summary>
    public static IReadOnlyList<CaptionCue> FromLrc(string? text, string? speaker = "lyric")
    {
        var cues = new List<CaptionCue>();
        if (string.IsNullOrWhiteSpace(text)) return cues;
        double offset = 0;
        int index = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            var stamps = new List<double>();
            int cursor = 0;
            while (cursor < line.Length && line[cursor] == '[')
            {
                int close = line.IndexOf(']', cursor);
                if (close < 0) break;
                string tag = line[(cursor + 1)..close];
                if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(tag[7..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                        offset = ms / 1000.0;
                }
                else if (TryParseStamp(tag, out double seconds))
                {
                    stamps.Add(seconds);
                }
                cursor = close + 1;
            }
            string body = line[cursor..].Trim();
            if (stamps.Count == 0 || body.Length == 0) continue;
            (string plain, List<double>? characterTimes) = ParseEnhanced(body);
            if (plain.Length == 0) continue;
            foreach (double stamp in stamps)
            {
                cues.Add(new CaptionCue
                {
                    Id = "lrc" + index++,
                    Text = plain,
                    At = Math.Max(0, stamp - offset),
                    Speaker = speaker,
                    CharacterTimes = characterTimes
                });
            }
        }
        return cues;
    }

    /// <summary>Parses SRT: an index line, a <c>start --&gt; end</c> line, then the text.</summary>
    public static IReadOnlyList<CaptionCue> FromSrt(string? text, string? speaker = "lyric")
    {
        var cues = new List<CaptionCue>();
        if (string.IsNullOrWhiteSpace(text)) return cues;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int index = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!TryParseSrtRange(lines[i], out double start, out double end)) continue;
            var body = new StringBuilder();
            for (int j = i + 1; j < lines.Length; j++)
            {
                string line = lines[j].Trim();
                if (line.Length == 0) break;
                if (TryParseSrtRange(line, out _, out _)) break;
                if (body.Length > 0) body.Append(' ');
                body.Append(line);
                i = j;
            }
            string joined = body.ToString().Trim();
            if (joined.Length == 0) continue;
            cues.Add(new CaptionCue
            {
                Id = "srt" + index++,
                Text = joined,
                At = Math.Max(0, start),
                End = end > start ? end : null,
                Speaker = speaker
            });
        }
        return cues;
    }

    /// <summary>
    /// Parses the dialogue lines of an ASS/SSA script. Only the timing and the text are taken: the file's own
    /// styling is deliberately ignored, because the caption's look belongs to the caption sheet.
    /// </summary>
    public static IReadOnlyList<CaptionCue> FromAss(string? text, string? speaker = "lyric")
    {
        var cues = new List<CaptionCue>();
        if (string.IsNullOrWhiteSpace(text)) return cues;
        bool inEvents = false;
        int index = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inEvents = line.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            // Dialogue: layer, start, end, style, name, marginL, marginR, marginV, effect, text
            string[] parts = line[9..].Split(',', 10);
            if (parts.Length < 10) continue;
            if (!TryParseStamp(parts[1].Trim(), out double start)) continue;
            if (!TryParseStamp(parts[2].Trim(), out double end)) continue;
            string body = StripAssMarkup(parts[9]);
            if (body.Length == 0) continue;
            cues.Add(new CaptionCue
            {
                Id = "ass" + index++,
                Text = body,
                At = Math.Max(0, start),
                End = end > start ? end : null,
                Speaker = string.IsNullOrWhiteSpace(parts[4]) ? speaker : parts[4].Trim()
            });
        }
        return cues;
    }

    /// <summary>Whether a file's content looks like SRT rather than LRC, for a file with the wrong name.</summary>
    public static bool LooksLikeSrt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string raw in text.Split('\n'))
        {
            if (raw.Contains("-->", StringComparison.Ordinal)) return true;
            if (raw.TrimStart().StartsWith('[')) return false;
        }
        return false;
    }

    /// <summary>
    /// Takes one enhanced-LRC line apart: the visible text, and the per-character times when the line carries them.
    /// A line without markers comes back whole with no times, which is what a plain LRC file looks like.
    /// </summary>
    private static (string Text, List<double>? Times) ParseEnhanced(string body)
    {
        if (!body.Contains('<')) return (body, null);
        var times = new List<double>();
        var text = new StringBuilder();
        int cursor = 0;
        while (cursor < body.Length)
        {
            if (body[cursor] == '<')
            {
                int close = body.IndexOf('>', cursor);
                if (close > cursor && TryParseStamp(body[(cursor + 1)..close], out double when))
                {
                    times.Add(Math.Max(0, when));
                    cursor = close + 1;
                    continue;
                }
            }
            text.Append(body[cursor]);
            cursor++;
        }
        string plain = text.ToString().Trim();
        // Times only mean something when there is one per character; otherwise the plain spacing is used instead.
        if (times.Count == 0 || times.Count != CaptionTimeline.CharacterCount(plain))
            return (plain, null);
        double first = times[0];
        for (int i = 0; i < times.Count; i++) times[i] = Math.Max(0, times[i] - first);
        return (plain, times);
    }

    /// <summary>Parses <c>mm:ss</c>, <c>mm:ss.xx</c>, <c>mm:ss,xxx</c> or <c>hh:mm:ss.xx</c> into seconds.</summary>
    private static bool TryParseStamp(string? value, out double seconds)
    {
        seconds = 0;
        string text = (value ?? string.Empty).Trim().Replace(',', '.');
        if (text.Length == 0) return false;
        string[] parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double piece)) return false;
            total = total * 60 + piece;
        }
        seconds = total;
        return true;
    }

    private static bool TryParseSrtRange(string? line, out double start, out double end)
    {
        start = 0;
        end = 0;
        string text = line ?? string.Empty;
        int arrow = text.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0) return false;
        return TryParseStamp(text[..arrow], out start) && TryParseStamp(text[(arrow + 3)..], out end);
    }

    /// <summary>Removes the <c>{\…}</c> override blocks ASS uses for inline styling.</summary>
    private static string StripAssMarkup(string value)
    {
        var text = new StringBuilder();
        int depth = 0;
        foreach (char character in value)
        {
            if (character == '{') { depth++; continue; }
            if (character == '}') { if (depth > 0) depth--; continue; }
            if (depth == 0) text.Append(character);
        }
        return text.ToString().Replace("\\N", " ").Replace("\\n", " ").Trim();
    }
}
