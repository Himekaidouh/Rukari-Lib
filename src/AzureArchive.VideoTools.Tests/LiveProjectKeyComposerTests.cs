using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

internal static class LiveProjectKeyComposerTests
{
    private const string GuidD = "12345678-1234-1234-1234-1234567890ab";

    public static void ComposeProducesDocumentedDeterministic24HexKey()
    {
        string key = LiveProjectKeyComposer.Compose(
            "未命名项目",
            "SavedTitle",
            GuidD);

        AssertEx.Equal(24, key.Length, key);
        AssertEx.True(key.All(Uri.IsHexDigit), key);
        AssertEx.True(
            key.All(character => !char.IsLower(character)),
            "the composed key must be upper hex");

        // Independent reference implementation of the documented input format.
        string source = $"aavt-live-project/v1\n未命名项目\nSavedTitle\n{GuidD}";
        string expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24];
        AssertEx.Equal(expected, key);

        // Whitespace framing on the raw interop strings must not fork keys.
        AssertEx.Equal(
            key,
            LiveProjectKeyComposer.Compose(
                " 未命名项目 ",
                " SavedTitle",
                $"{GuidD} "));
    }

    public static void ComposerIsDeterministicAndSeparatesDistinctTriples()
    {
        string first = LiveProjectKeyComposer.Compose("a", "b", GuidD);
        AssertEx.Equal(first, LiveProjectKeyComposer.Compose("a", "b", GuidD));
        AssertEx.False(
            first.Equals(LiveProjectKeyComposer.Compose("a", "b2", GuidD), StringComparison.Ordinal),
            "a different saved name must compose a different key");
        AssertEx.False(
            first.Equals(LiveProjectKeyComposer.Compose("a2", "b", GuidD), StringComparison.Ordinal),
            "a different project name must compose a different key");
        AssertEx.False(
            first.Equals(
                LiveProjectKeyComposer.Compose("a", "b", "ffffffff-1234-1234-1234-1234567890ab"),
                StringComparison.Ordinal),
            "a different entry-node guid must compose a different key");
    }

    public static void ComposerRejectsAllEmptyWhitespaceAndMissingInputs()
    {
        AssertEx.False(LiveProjectKeyComposer.IsValidInput(null, "b", GuidD));
        AssertEx.False(LiveProjectKeyComposer.IsValidInput("a", null, GuidD));
        AssertEx.False(LiveProjectKeyComposer.IsValidInput("a", "b", null));
        AssertEx.False(LiveProjectKeyComposer.IsValidInput(string.Empty, "b", GuidD));
        AssertEx.False(LiveProjectKeyComposer.IsValidInput("a", "   ", GuidD));
        AssertEx.False(LiveProjectKeyComposer.IsValidInput(string.Empty, string.Empty, string.Empty),
            "an all-empty triple is unavailable and must never be guessed");
        AssertEx.True(LiveProjectKeyComposer.IsValidInput("a", "b", GuidD));

        AssertThrows<ArgumentException>(() => LiveProjectKeyComposer.Compose("", "", ""));
    }

    public static void EffectiveKeyPrecedenceRootedPathBeatsAdoptedLiveKey()
    {
        var state = new EffectiveProjectKeyState();
        AssertEx.False(state.HasCanonicalKey);
        AssertEx.Equal("_unknown_project", state.EffectiveKey);

        const string liveKey = "0123456789ABCDEF01234567";
        AssertEx.True(state.AdoptLiveProjectKey(liveKey));
        AssertEx.Equal(liveKey, state.EffectiveKey);
        AssertEx.True(state.HasCanonicalKey);
        AssertEx.True(state.ConsumeRotated(), "first adoption rotates away from the sentinel");

        string path = @"C:\data\projects\demo.aap";
        state.ObserveRootedPath(path);
        AssertEx.Equal(ReferenceHash(path), state.EffectiveKey);
        AssertEx.True(state.ConsumeRotated(), "source A must displace source B exactly once");
        AssertEx.False(state.ConsumeRotated(), "the rotation edge must be edge-triggered");

        // Adoption underneath a rooted path changes nothing observable.
        AssertEx.False(state.AdoptLiveProjectKey("FFFFFFFFFFFFFFFFFFFFFFFF"));
        AssertEx.Equal(ReferenceHash(path), state.EffectiveKey);
        AssertEx.False(state.ConsumeRotated());

        // Invalid adoptions are ignored outright.
        AssertEx.False(state.AdoptLiveProjectKey("not-a-key"));
        AssertEx.False(state.AdoptLiveProjectKey(null));
        AssertEx.Equal(ReferenceHash(path), state.EffectiveKey);
    }

    public static void PathArrivalRotatesEffectiveKeyExactlyOnce()
    {
        var state = new EffectiveProjectKeyState();
        AssertEx.True(state.AdoptLiveProjectKey("AAAAAAAAAAAAAAAAAAAAAAAA"));
        AssertEx.True(state.ConsumeRotated());

        state.ObserveRootedPath(@"C:\p\a.aap");
        AssertEx.True(state.ConsumeRotated(), "trusted path arrival rotates exactly once");
        AssertEx.False(state.ConsumeRotated());

        // Idempotent re-reads cause no churn.
        state.ObserveRootedPath(@"C:\p\a.aap");
        AssertEx.False(state.ConsumeRotated());
        AssertEx.Equal(ReferenceHash(@"C:\p\a.aap"), state.EffectiveKey);

        // A replaced rooted path rotates exactly once more.
        state.ObserveRootedPath(@"C:\p\b.aap");
        AssertEx.True(state.ConsumeRotated());
        AssertEx.Equal(ReferenceHash(@"C:\p\b.aap"), state.EffectiveKey);
        AssertEx.False(state.ConsumeRotated());
    }

    public static void UnsavedToSavedAdoptedKeyRotationIsSingleAndDeterministic()
    {
        var state = new EffectiveProjectKeyState();

        // While any StudioCommon input is missing the adoption fails closed.
        AssertEx.False(state.AdoptLiveProjectKey(string.Empty));
        AssertEx.Equal("_unknown_project", state.EffectiveKey);
        AssertEx.False(state.HasCanonicalKey);

        const string unsavedKey = "BBBBBBBBBBBBBBBBBBBBBBBB";
        const string savedKey = "CCCCCCCCCCCCCCCCCCCCCCCC";
        AssertEx.True(state.AdoptLiveProjectKey(unsavedKey));
        AssertEx.True(state.ConsumeRotated(), "unsaved adoption rotates off the sentinel");

        // Re-confirming the identical triple must not churn.
        AssertEx.False(state.AdoptLiveProjectKey(unsavedKey));
        AssertEx.False(state.ConsumeRotated());

        // Saving under a new title rotates exactly once, deterministically.
        AssertEx.True(state.AdoptLiveProjectKey(savedKey));
        AssertEx.True(state.ConsumeRotated());
        AssertEx.False(state.ConsumeRotated());
        AssertEx.Equal(savedKey, state.EffectiveKey);
        AssertEx.True(state.HasCanonicalKey);

        AssertEx.False(state.AdoptLiveProjectKey(savedKey));
        AssertEx.Equal(savedKey, state.EffectiveKey);
    }

    private static string ReferenceHash(string normalizedPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..24];

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
