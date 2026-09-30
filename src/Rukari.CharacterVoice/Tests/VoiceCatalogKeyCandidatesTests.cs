using Rukari.CharacterVoice.Core;

/// <summary>
/// The catalog is asked which spelling of a manifest entry it knows, so the candidate list has to contain every
/// plausible one, in a stable order, without inventing keys that could name a different resource.
/// </summary>
internal static class VoiceCatalogKeyCandidatesTests
{
    internal static void RunAll()
    {
        ListsEverySpellingOfAWindowsVoicePath();
        KeepsForwardSlashPathsAsWritten();
        IgnoresEmptyAndOversizedInput();
        NeverInventsAKeyForAMissingFileName();
    }

    private static void ListsEverySpellingOfAWindowsVoicePath()
    {
        IReadOnlyList<string> candidates =
            VoiceCatalogKeyCandidates.For(@"voices\a3734052-c014-48be-b20a-2b5786702c7c.ogg");
        Equal(4, candidates.Count);
        Equal("voices/a3734052-c014-48be-b20a-2b5786702c7c.ogg", candidates[0]);
        Equal(@"voices\a3734052-c014-48be-b20a-2b5786702c7c.ogg", candidates[1]);
        Equal("a3734052-c014-48be-b20a-2b5786702c7c.ogg", candidates[2]);
        Equal("a3734052-c014-48be-b20a-2b5786702c7c", candidates[3]);
    }

    private static void KeepsForwardSlashPathsAsWritten()
    {
        IReadOnlyList<string> candidates = VoiceCatalogKeyCandidates.For("voices/keep-1.ogg");
        // The path is already normalized, so the raw spelling must not be repeated.
        Equal(3, candidates.Count);
        Equal("voices/keep-1.ogg", candidates[0]);
        Equal("keep-1.ogg", candidates[1]);
        Equal("keep-1", candidates[2]);
    }

    private static void IgnoresEmptyAndOversizedInput()
    {
        Equal(0, VoiceCatalogKeyCandidates.For(null).Count);
        Equal(0, VoiceCatalogKeyCandidates.For("   ").Count);
        Equal(0, VoiceCatalogKeyCandidates.For(new string('a', 513)).Count);
    }

    private static void NeverInventsAKeyForAMissingFileName()
    {
        // A trailing separator has no file name: the path itself stays, but no empty stem is added.
        IReadOnlyList<string> candidates = VoiceCatalogKeyCandidates.For("voices/");
        True(candidates.All(candidate => candidate.Length != 0), "An empty candidate must never be produced.");
        True(candidates.Contains("voices/"), "The path as written must stay the first candidate.");
    }

    private static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
}
