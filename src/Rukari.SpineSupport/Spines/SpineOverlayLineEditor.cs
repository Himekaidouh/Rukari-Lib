using System.Globalization;
using Rukari.Lib.Spines;

namespace Rukari.SpineSupport.Spines;

/// <summary>
/// The console's writing half (2026-09-21): turns a chosen overlay into the line the author would
/// have typed, and puts it where it belongs in the line's additional-prompt text.
/// <para>
/// Pure text, on purpose: this is the part that can silently eat a directive the author wrote, so it
/// is kept out of the game assembly and covered by its own tests. Everything else about writing —
/// the revision check, the readback, the rollback — belongs to the shared editor transaction.
/// </para>
/// </summary>
public static class SpineOverlayLineEditor
{
    private const string DirectivePrefix = "#aavt;spine;";

    /// <summary>One play directive, with every choice written out so the line explains itself.</summary>
    public static string BuildPlayLine(int publicSlot, string animation, int trackIndex, bool loop, bool hold)
    {
        // net6 has no ArgumentException.ThrowIfNullOrWhiteSpace, so the guard is explicit.
        if (string.IsNullOrWhiteSpace(animation))
        {
            throw new ArgumentException("An overlay line needs an animation name.", nameof(animation));
        }

        string line = $"{DirectivePrefix}{publicSlot.ToString(CultureInfo.InvariantCulture)};{animation.Trim()}"
            + $";track={trackIndex.ToString(CultureInfo.InvariantCulture)}"
            + $";loop={(loop ? "true" : "false")}";
        return loop ? line : line + $";hold={(hold ? "true" : "false")}";
    }

    /// <summary>The clear directive for one track; the track is always written out here.</summary>
    public static string BuildClearLine(int publicSlot, int trackIndex) =>
        $"{DirectivePrefix}{publicSlot.ToString(CultureInfo.InvariantCulture)};clear"
        + $";track={trackIndex.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Replaces the overlay directive that claims the same slot and track, or appends the new one.
    /// Every other line — the author's text and their other directives — is left exactly as it was,
    /// including which line terminator the text uses.
    /// </summary>
    public static string Upsert(string? text, string line, int publicSlot, int trackIndex)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new ArgumentException("An overlay line cannot be empty.", nameof(line));
        }

        string source = text ?? string.Empty;
        string[] lines = source.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string raw = lines[index];
            string content = raw.Trim();
            if (!Claims(content, publicSlot, trackIndex)) continue;
            lines[index] = raw.EndsWith('\r') ? line + "\r" : line;
            return string.Join('\n', lines);
        }

        if (source.Length == 0) return line;
        return source.EndsWith('\n') ? source + line : source + Newline(source) + line;
    }

    /// <summary>Whether one line is an overlay directive for exactly this slot and track.</summary>
    public static bool Claims(string line, int publicSlot, int trackIndex)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string content = line.Trim();
        if (!content.StartsWith(DirectivePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = content.Split(';');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
        {
            return false;
        }

        return slot == publicSlot && TrackOf(parts) == trackIndex;
    }

    /// <summary>
    /// The track one overlay line claims, defaulted the same way the parser defaults it, so a
    /// directive that never mentioned a track is still found and replaced instead of being duplicated.
    /// </summary>
    private static int TrackOf(string[] parts)
    {
        for (int index = 3; index < parts.Length; index++)
        {
            string token = parts[index].Trim();
            if (!token.StartsWith("track=", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(
                token[6..].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int track)
                ? track
                : SpineOverlayTracks.Default;
        }

        return SpineOverlayTracks.Default;
    }

    private static string Newline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
