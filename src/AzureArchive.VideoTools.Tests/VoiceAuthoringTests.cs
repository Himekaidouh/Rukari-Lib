using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Tests;

internal static class VoiceAuthoringTests
{
    public static void AddsCanonicalVoiceAndReportsDocumentRevisions()
    {
        const string source = "#wait;500\r\n#aavt;continue";
        var composer = new EditorCommandDocumentComposer();
        string revision = EditorCommandDocumentComposer.Revision(source);
        EditorCommandVoiceEdit edit = AssertEx.NotNull(
            composer.SetVoice(source, revision.ToLowerInvariant(), "凯伊\\Talk.v2 01").Value);

        AssertEx.Equal(source + "\r\n#aavt;voice;凯伊/Talk.v2 01", edit.UpdatedText);
        AssertEx.Equal("凯伊/Talk.v2 01", edit.ResourceKey);
        AssertEx.Equal(revision, edit.ExpectedRevisionSha256);
        AssertEx.Equal(EditorCommandDocumentComposer.Revision(edit.UpdatedText), edit.ResultRevisionSha256);
        AssertEx.False(edit.ReplacedExisting);
        AssertEx.False(edit.AlreadyPresent);
        AssertEx.True(composer.Read(edit.UpdatedText).Success);
    }

    public static void ReplacesOnlyVoiceLineAndPreservesMixedNewlines()
    {
        const string source = "  说明 #aavt;voice;inline  \r\n"
            + "#aavt;char;2;set;x=10;duration=0;easing=linear\n"
            + "\r"
            + "  #AAVT ; VOICE ; old.ogg  \r"
            + "#st;[0,0];instant;48;\r\n"
            + "#bgshake  ";
        const string expected = "  说明 #aavt;voice;inline  \r\n"
            + "#aavt;char;2;set;x=10;duration=0;easing=linear\n"
            + "\r"
            + "#aavt;voice;新语音/Talk 02\r"
            + "#st;[0,0];instant;48;\r\n"
            + "#bgshake  ";
        var composer = new EditorCommandDocumentComposer();
        EditorCommandVoiceEdit edit = AssertEx.NotNull(composer.SetVoice(
            source, EditorCommandDocumentComposer.Revision(source), "新语音/Talk 02").Value);

        AssertEx.Equal(expected, edit.UpdatedText);
        AssertEx.True(edit.ReplacedExisting);
        AssertEx.False(edit.AlreadyPresent);
        AssertEx.Equal(1, AssertEx.NotNull(composer.Read(edit.UpdatedText).Value).Commands.Count);
    }

    public static void RemovesOnlyVoiceLineIncludingItsOwnTerminator()
    {
        var cases = new Dictionary<string, string>
        {
            ["#aavt;voice;old"] = "",
            ["#aavt;voice;old\r\n#wait;500"] = "#wait;500",
            ["#wait;500\n#aavt;voice;old\r\n#bgshake"] = "#wait;500\n#bgshake",
            ["#wait;500\r#aavt;voice;old"] = "#wait;500\r",
        };
        var composer = new EditorCommandDocumentComposer();
        foreach ((string source, string expected) in cases)
        {
            EditorCommandVoiceEdit edit = AssertEx.NotNull(composer.SetVoice(
                source, EditorCommandDocumentComposer.Revision(source), null).Value);
            AssertEx.Equal(expected, edit.UpdatedText);
            AssertEx.True(edit.ResourceKey is null);
            AssertEx.True(edit.ReplacedExisting);
            AssertEx.False(edit.AlreadyPresent);
            AssertEx.False(VoiceDirectivePolicy.Parse(edit.UpdatedText).HasDirective);
        }
    }

    public static void RepeatedVoiceWritesAndAbsentClearsAreIdempotent()
    {
        var composer = new EditorCommandDocumentComposer();
        foreach (string source in new[] { "", "#wait;500\r\n", "#bgshake\n\n" })
        {
            string revision = EditorCommandDocumentComposer.Revision(source);
            EditorCommandVoiceEdit cleared = AssertEx.NotNull(composer.SetVoice(source, revision, null).Value);
            AssertEx.True(cleared.AlreadyPresent);
            AssertEx.False(cleared.ReplacedExisting);
            AssertEx.Equal(source, cleared.UpdatedText);
            AssertEx.Equal(revision, cleared.ResultRevisionSha256);
        }

        const string existing = "#wait;500\n#aavt;voice;凯伊/Talk 01\r\n#bgshake";
        string existingRevision = EditorCommandDocumentComposer.Revision(existing);
        EditorCommandVoiceEdit unchanged = AssertEx.NotNull(composer.SetVoice(
            existing, existingRevision, "凯伊/Talk 01").Value);
        AssertEx.True(unchanged.AlreadyPresent);
        AssertEx.True(unchanged.ReplacedExisting);
        AssertEx.Equal(existing, unchanged.UpdatedText);
        AssertEx.Equal(existingRevision, unchanged.ResultRevisionSha256);
    }

    public static void AppendingVoiceRespectsExistingDocumentTermination()
    {
        var cases = new Dictionary<string, string>
        {
            [""] = "#aavt;voice;new",
            ["#wait;500"] = "#wait;500\n#aavt;voice;new",
            ["#wait;500\r"] = "#wait;500\r#aavt;voice;new",
            ["#wait;500\r\n"] = "#wait;500\r\n#aavt;voice;new",
            ["#wait;500\n\n"] = "#wait;500\n\n#aavt;voice;new",
        };
        var composer = new EditorCommandDocumentComposer();
        foreach ((string source, string expected) in cases)
        {
            EditorCommandVoiceEdit edit = AssertEx.NotNull(composer.SetVoice(
                source, EditorCommandDocumentComposer.Revision(source), "new").Value);
            AssertEx.Equal(expected, edit.UpdatedText);
        }
    }

    public static void StaleRevisionsRejectAddReplaceRemoveAndNoOp()
    {
        var composer = new EditorCommandDocumentComposer();
        foreach (string source in new[] { "#wait;500", "#aavt;voice;old" })
        {
            foreach (string? key in new string?[] { null, "old", "new" })
            {
                var rejected = composer.SetVoice(source, new string('0', 64), key);
                AssertEx.False(rejected.Success);
                AssertEx.True(rejected.Value is null);
            }
        }
    }

    public static void InvalidSourceDocumentsCannotBeSilentlyRepaired()
    {
        var composer = new EditorCommandDocumentComposer();
        foreach (string source in new[]
        {
            "#aavt;voice;",
            "#aavt;voice;one;two",
            "#aavt;voice;one\n#aavt;voice;one",
            "#aavt;voice;one\n#aavt;voice;two",
            "#aavt;camera;set;zoom=invalid",
            "#st;[0,0];instant;48",
        })
        {
            foreach (string? key in new string?[] { null, "new" })
            {
                var rejected = composer.SetVoice(source, EditorCommandDocumentComposer.Revision(source), key);
                AssertEx.False(rejected.Success);
                AssertEx.True(rejected.Value is null);
            }
        }
    }

    public static void CatalogKeysCannotInjectLinesOrEscapeResourceNamespace()
    {
        const string source = "#wait;500\n#aavt;voice;old";
        var composer = new EditorCommandDocumentComposer();
        foreach (string key in new[]
        {
            "", " ", " leading", "trailing ", "one;two", "one\n#bgshake", "one\r#wait;1", "one\t", "one\0",
            "/absolute", "\\absolute", "C:\\voice", "https://voice", "../voice", "a/./voice", "a//voice",
        })
        {
            var rejected = composer.SetVoice(source, EditorCommandDocumentComposer.Revision(source), key);
            AssertEx.False(rejected.Success);
            AssertEx.True(rejected.Value is null);
        }
    }

    public static void CatalogStemsEndingInAudioExtensionsRoundTripWithoutBeingStripped()
    {
        var composer = new EditorCommandDocumentComposer();
        foreach (string key in new[] { "foo.wav", "Dialogue.MP3", "凯伊/Talk.ogg", "foo.wav.ogg" })
        {
            EditorCommandVoiceEdit edit = AssertEx.NotNull(composer.SetVoice(
                "", EditorCommandDocumentComposer.Revision(""), key).Value);
            AssertEx.Equal(key, edit.ResourceKey);
            AssertEx.Equal("#aavt;voice;" + key + ".ogg", edit.UpdatedText);
            AssertEx.Equal(key, VoiceDirectivePolicy.Parse(edit.UpdatedText).ResourceKey);

            EditorCommandVoiceEdit repeated = AssertEx.NotNull(composer.SetVoice(
                edit.UpdatedText, edit.ResultRevisionSha256, key).Value);
            AssertEx.True(repeated.AlreadyPresent);
            AssertEx.Equal(edit.UpdatedText, repeated.UpdatedText);
        }
    }

    public static void VoiceEditsHonorLegacyAliasValidationSetting()
    {
        const string source = "#char;2;invalid-operation\r\n#wait;500";
        var composer = new EditorCommandDocumentComposer();
        string revision = EditorCommandDocumentComposer.Revision(source);
        AssertEx.False(composer.SetVoice(source, revision, "new", acceptLegacyCharAlias: true).Success);
        EditorCommandVoiceEdit edit = AssertEx.NotNull(composer.SetVoice(
            source, revision, "new", acceptLegacyCharAlias: false).Value);
        AssertEx.Equal(source + "\r\n#aavt;voice;new", edit.UpdatedText);
    }
}
