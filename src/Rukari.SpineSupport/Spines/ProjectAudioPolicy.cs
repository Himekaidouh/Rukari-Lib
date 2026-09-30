namespace Rukari.SpineSupport.Spines;

/// <summary>
/// Recognises the audio identifiers that only a project produces, never the game.
///
/// <para>
/// Two shapes matter. A converted lobby keeps the source game's event audio, so its identifier is either the event
/// name (<c>Sound/CH0345_MemorialLobby_1_1</c>) or the file it names (<c>CH0345_MemorialLobby_1_1.wav</c>). A line
/// that was given a voice stores a bare GUID, which the editor resolves to <c>voices/&lt;guid&gt;.ogg</c> inside the
/// project — when that file is gone the GUID is all that is left.
/// </para>
///
/// <para>
/// The game's own identifiers — <c>SE_Bell_04</c>, a shipped voice id — carry no extension and no <c>Sound/</c>
/// prefix, and a bare GUID is never one of them. This answers only that one question and decides nothing else: the
/// caller still has to find out whether the resource exists before it touches anything.
/// </para>
/// </summary>
internal static class ProjectAudioPolicy
{
    private static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3", ".m4a", ".aac" };

    /// <summary>How the source lobby projects prefix their sound events.</summary>
    internal const string SoundEventPrefix = "Sound/";

    /// <summary>True when the identifier could only have come from a project's own records.</summary>
    internal static bool IsProjectOnlyIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return false;
        string value = identifier.Trim();
        if (value.StartsWith(SoundEventPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (value.StartsWith("voices/", StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikeGuid(value)) return true;
        foreach (string extension in AudioExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>8-4-4-4-12 hexadecimal, the form the editor writes for a line's voice.</summary>
    internal static bool LooksLikeGuid(string? value)
    {
        if (value is null || value.Length != 36) return false;
        for (int index = 0; index < 36; index++)
        {
            char character = value[index];
            bool separator = index is 8 or 13 or 18 or 23;
            if (separator)
            {
                if (character != '-') return false;
                continue;
            }

            bool hex = character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }

        return true;
    }
}
