namespace Rukari.CharacterVoice.Core;

/// <summary>
/// The spellings a project voice override can have when it is handed back to the editor's resource catalog.
///
/// <para>
/// The project manifest stores <c>voices\&lt;guid&gt;.ogg</c>. The catalog indexes resources by a key that is not
/// documented anywhere reachable from outside the game, and the plausible forms differ: the forward-slash relative
/// path, the path exactly as written, the file name, or the bare stem. Listing them, most likely first, lets a
/// diagnostic ask the running editor which one it accepts — instead of guessing and deleting the wrong entry.
/// </para>
/// </summary>
internal static class VoiceCatalogKeyCandidates
{
    private const int MaximumLength = 512;

    /// <summary>Plausible catalog keys for one manifest entry, de-duplicated, most likely spelling first.</summary>
    internal static IReadOnlyList<string> For(string? relativePath)
    {
        var candidates = new List<string>(4);
        string raw = (relativePath ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.Length > MaximumLength) return candidates;

        string normalized = raw.Replace('\\', '/');
        Add(candidates, normalized);
        Add(candidates, raw);
        string fileName = Path.GetFileName(normalized);
        Add(candidates, fileName);
        if (fileName.Length != 0) Add(candidates, Path.GetFileNameWithoutExtension(fileName));
        return candidates;
    }

    private static void Add(List<string> candidates, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (string existing in candidates)
        {
            if (string.Equals(existing, value, StringComparison.Ordinal)) return;
        }

        candidates.Add(value);
    }
}
