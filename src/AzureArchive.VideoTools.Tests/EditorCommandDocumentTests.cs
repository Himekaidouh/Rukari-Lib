using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Tests;

internal static class EditorCommandDocumentTests
{
    public static void ReadsAavtDocumentWithoutChangingOfficialText()
    {
        const string source =
            "#wait;1200\r\n"
            + "#aavt;continue\r\n"
            + "#bgshake\r\n"
            + "#aavt;char;4;set;x=120;duration=300;easing=easeOut";
        var composer = new EditorCommandDocumentComposer();

        EditorCommandDocument document = AssertEx.NotNull(composer.Read(source).Value);

        AssertEx.Equal(source, document.Text);
        AssertEx.Equal(2, document.OfficialLineCount);
        AssertEx.Equal(2, document.AavtLineCount);
        AssertEx.True(document.HasContinueDirective);
        AssertEx.Equal(1, document.Commands.Count);
        AssertEx.Equal(4, document.Commands[0].PublicSlot);
    }

    public static void UpsertsOneSlotAndPreservesEveryOtherLine()
    {
        const string source =
            "#wait;1200\r\n"
            + "  #aavt;char;2;set;x=10;duration=0;easing=linear  \r\n"
            + "\r\n"
            + "#aavt;continue\r\n"
            + "#aavt;char;4;set;x=400;duration=200;easing=easeIn\r\n"
            + "#bgshake";
        var composer = new EditorCommandDocumentComposer();
        string revision = AssertEx.NotNull(composer.Read(source).Value).RevisionSha256;

        EditorCommandEditPreview preview = AssertEx.NotNull(composer.PreviewUpsert(
            source,
            revision,
            "#aavt;char;2;set;x=-220.5;y=100;duration=600;easing=easeOut").Value);

        const string expected =
            "#wait;1200\r\n"
            + "#aavt;char;2;set;x=-220.5;y=100;duration=600;easing=easeOut\r\n"
            + "\r\n"
            + "#aavt;continue\r\n"
            + "#aavt;char;4;set;x=400;duration=200;easing=easeIn\r\n"
            + "#bgshake";
        AssertEx.Equal(expected, preview.UpdatedText);
        AssertEx.True(preview.ReplacedExisting);
        AssertEx.Equal(2, preview.PublicSlot);
    }

    public static void AppendsPendingWithoutTimingAndRejectsStaleRevision()
    {
        const string source = "#wait;500\n#bgshake";
        var composer = new EditorCommandDocumentComposer();
        string revision = AssertEx.NotNull(composer.Read(source).Value).RevisionSha256;

        EditorCommandEditPreview preview = AssertEx.NotNull(composer.PreviewUpsert(
            source,
            revision,
            "#aavt;charPending;5;set;x=1750;y=-900").Value);

        AssertEx.Equal(
            "#wait;500\n#bgshake\n#aavt;charPending;5;set;x=1750;y=-900",
            preview.UpdatedText);
        AssertEx.False(preview.ReplacedExisting);
        AssertEx.False(composer.PreviewUpsert(
            source,
            new string('0', 64),
            "#aavt;charPending;5;set;x=1750").Success);
        AssertEx.False(composer.PreviewUpsert(
            source,
            revision,
            "#aavt;charPending;5;set;x=1750;duration=600").Success);
    }

    public static void UpsertsSingletonCameraWithoutReplacingCharacters()
    {
        const string source =
            "#wait;500\n"
            + "#aavt;char;1;set;x=100;duration=0;easing=linear\n"
            + "#aavt;camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut\n"
            + "#bgshake";
        var composer = new EditorCommandDocumentComposer();
        string revision = AssertEx.NotNull(composer.Read(source).Value).RevisionSha256;

        EditorCommandEditPreview preview = AssertEx.NotNull(composer.PreviewUpsert(
            source,
            revision,
            "#aavt;camera;set;x=400;y=-120;zoom=1.5;duration=800;easing=easeInOut").Value);

        AssertEx.True(preview.ReplacedExisting);
        AssertEx.Equal(0, preview.PublicSlot);
        AssertEx.True(preview.UpdatedText.Contains(
            "#aavt;char;1;set;x=100;duration=0;easing=linear",
            StringComparison.Ordinal));
        AssertEx.True(preview.UpdatedText.Contains(
            "#aavt;camera;set;x=400;y=-120;zoom=1.5;duration=800;easing=easeInOut",
            StringComparison.Ordinal));
        AssertEx.False(preview.UpdatedText.Contains(
            "#aavt;camera;set;x=0;y=0;zoom=1.2",
            StringComparison.Ordinal));
    }

    public static void SetContinueAppendsRemovesAndIsIdempotent()
    {
        const string withoutContinue =
            "#wait;1200\r\n"
            + "#aavt;char;4;set;x=120;duration=300;easing=easeOut";
        const string withContinue =
            "#wait;1200\r\n"
            + "#aavt;char;4;set;x=120;duration=300;easing=easeOut\r\n"
            + "#aavt;continue";
        var composer = new EditorCommandDocumentComposer();
        string plainRevision = AssertEx.NotNull(
            composer.Read(withoutContinue).Value).RevisionSha256;

        // Enable: appends the standalone line and preserves everything else.
        EditorCommandContinueEdit enabled = composer.SetContinue(
            withoutContinue,
            plainRevision,
            enabled: true);
        AssertEx.False(enabled.AlreadyPresent);
        AssertEx.Equal(withContinue, enabled.UpdatedText);
        AssertEx.True(enabled.ContinueEnabled);
        AssertEx.False(string.Equals(
            enabled.ExpectedRevisionSha256,
            enabled.ResultRevisionSha256,
            StringComparison.Ordinal));

        // Enable again: idempotent no-op.
        EditorCommandContinueEdit enabledAgain = composer.SetContinue(
            enabled.UpdatedText,
            enabled.ResultRevisionSha256,
            enabled: true);
        AssertEx.True(enabledAgain.AlreadyPresent);
        AssertEx.Equal(enabled.UpdatedText, enabledAgain.UpdatedText);

        // Disable: removes the continue line (and only that line), even with
        // stray surrounding whitespace and case differences.
        string withWhitespace = withContinue + "\r\n   #AAVT;Continue  ";
        string spacedRevision = AssertEx.NotNull(
            composer.Read(withWhitespace).Value).RevisionSha256;
        EditorCommandContinueEdit disabled = composer.SetContinue(
            withWhitespace,
            spacedRevision,
            enabled: false);
        AssertEx.False(disabled.AlreadyPresent);
        AssertEx.False(disabled.UpdatedText.Contains(
            "#aavt;continue",
            StringComparison.OrdinalIgnoreCase));
        AssertEx.True(disabled.UpdatedText.Contains(
            "#aavt;char;4;set;x=120;duration=300;easing=easeOut",
            StringComparison.Ordinal));
        AssertEx.True(disabled.UpdatedText.Contains("#wait;1200", StringComparison.Ordinal));
        EditorCommandDocument disabledRead = AssertEx.NotNull(
            composer.Read(disabled.UpdatedText).Value);
        AssertEx.False(disabledRead.HasContinueDirective);
        AssertEx.Equal(1, disabledRead.Commands.Count);

        // Disable again: idempotent no-op.
        EditorCommandContinueEdit disabledAgain = composer.SetContinue(
            disabled.UpdatedText,
            disabled.ResultRevisionSha256,
            enabled: false);
        AssertEx.True(disabledAgain.AlreadyPresent);
    }

    public static void ParsesOfficialScreenTextAndRequiresItsFinalSemicolon()
    {
        ScreenTextDirective centered = AssertEx.NotNull(
            ScreenTextDirectiveCodec.ParseLine(
                "  #STM;[-420.5,180];Smooth;64;  ").Value);

        AssertEx.Equal(-420.5f, centered.X);
        AssertEx.Equal(180f, centered.Y);
        AssertEx.Equal(ScreenTextAlignment.Center, centered.Alignment);
        AssertEx.Equal(ScreenTextRevealMode.Smooth, centered.RevealMode);
        AssertEx.Equal(64, centered.FontSize);
        AssertEx.Equal("#stm;[-420.5,180];smooth;64;", centered.ToOfficialDirective());
        AssertEx.False(ScreenTextDirectiveCodec.ParseLine(
            "#st;[0,0];serial;48").Success);
        AssertEx.False(ScreenTextDirectiveCodec.ParseLine(
            "#st;[0,0];unknown;48;").Success);
    }

    public static void UpsertsOfficialScreenTextWithoutTouchingOtherDirectives()
    {
        const string source =
            "#wait;500\r\n"
            + "  #st;[100,200];instant;48;  \r\n"
            + "#aavt;char;2;set;x=400;duration=0;easing=linear\r\n"
            + "#bgshake";
        var composer = new EditorCommandDocumentComposer();
        EditorCommandDocument before = AssertEx.NotNull(composer.Read(source).Value);

        var directive = new ScreenTextDirective(
            -320f,
            140.25f,
            ScreenTextAlignment.Center,
            ScreenTextRevealMode.Serial,
            72);
        EditorCommandScreenTextEdit edit = AssertEx.NotNull(composer.SetScreenText(
            source,
            before.RevisionSha256,
            directive).Value);

        const string expected =
            "#wait;500\r\n"
            + "#stm;[-320,140.25];serial;72;\r\n"
            + "#aavt;char;2;set;x=400;duration=0;easing=linear\r\n"
            + "#bgshake";
        AssertEx.Equal(expected, edit.UpdatedText);
        AssertEx.True(edit.ReplacedExisting);
        AssertEx.False(edit.AlreadyPresent);
        EditorCommandDocument after = AssertEx.NotNull(
            composer.Read(edit.UpdatedText).Value);
        AssertEx.Equal(1, after.ScreenTextLines.Count);
        AssertEx.Equal(ScreenTextAlignment.Center,
            after.ScreenTextLines[0].Directive.Alignment);
        AssertEx.Equal(1, after.Commands.Count);
    }

    public static void RemovesScreenTextAndRefusesToCollapseMultipleAuthoredLines()
    {
        const string multiple =
            "#st;[0,0];instant;48;\n"
            + "#wait;500\n"
            + "#stm;[100,100];serial;56;";
        var composer = new EditorCommandDocumentComposer();
        EditorCommandDocument authored = AssertEx.NotNull(composer.Read(multiple).Value);

        AssertEx.False(composer.SetScreenText(
            multiple,
            authored.RevisionSha256,
            new ScreenTextDirective(
                0f,
                0f,
                ScreenTextAlignment.Left,
                ScreenTextRevealMode.Instant,
                48)).Success);
        AssertEx.False(composer.SetScreenText(
            multiple,
            authored.RevisionSha256,
            directive: null).Success);

        const string source = "#st;[0,0];instant;48;\n#wait;500";
        EditorCommandDocument current = AssertEx.NotNull(composer.Read(source).Value);
        EditorCommandScreenTextEdit removed = AssertEx.NotNull(composer.SetScreenText(
            source,
            current.RevisionSha256,
            directive: null).Value);
        AssertEx.Equal("#wait;500", removed.UpdatedText);
        AssertEx.Equal(0, AssertEx.NotNull(
            composer.Read(removed.UpdatedText).Value).ScreenTextLines.Count);

        EditorCommandScreenTextEdit removedAgain = AssertEx.NotNull(
            composer.SetScreenText(
                removed.UpdatedText,
                removed.ResultRevisionSha256,
                directive: null).Value);
        AssertEx.True(removedAgain.AlreadyPresent);
    }

    public static void AddsOfficialClearScreenTextWithoutChangingOtherLines()
    {
        const string source =
            "#wait;500\r\n"
            + "#st;[100,200];instant;48;\r\n"
            + "#aavt;char;2;set;x=400;duration=0;easing=linear";
        var composer = new EditorCommandDocumentComposer();
        EditorCommandDocument before = AssertEx.NotNull(composer.Read(source).Value);
        AssertEx.False(before.HasClearScreenTextDirective);

        EditorCommandClearScreenTextEdit edit = AssertEx.NotNull(
            composer.AddClearScreenText(source, before.RevisionSha256).Value);
        AssertEx.Equal(source + "\r\n#clearST", edit.UpdatedText);
        AssertEx.False(edit.AlreadyPresent);

        EditorCommandDocument after = AssertEx.NotNull(
            composer.Read(edit.UpdatedText).Value);
        AssertEx.True(after.HasClearScreenTextDirective);
        AssertEx.Equal(1, after.ScreenTextLines.Count);
        AssertEx.Equal(1, after.Commands.Count);

        EditorCommandClearScreenTextEdit repeated = AssertEx.NotNull(
            composer.AddClearScreenText(
                edit.UpdatedText,
                edit.ResultRevisionSha256).Value);
        AssertEx.True(repeated.AlreadyPresent);
        AssertEx.Equal(edit.UpdatedText, repeated.UpdatedText);
        AssertEx.False(composer.AddClearScreenText(
            edit.UpdatedText,
            before.RevisionSha256).Success);
    }

    public static void UndoStoreRequiresTheExactAppliedSceneRevision()
    {
        const string before = "#wait;500";
        const string applied = "#wait;500\n#aavt;char;2;set;x=400;duration=600;easing=easeInOut";
        var store = new EditorCommandUndoStore();

        EditorCommandUndoRecord record = AssertEx.NotNull(store.Arm(
            "project/node/scene-2",
            before,
            applied).Value);

        AssertEx.True(store.CanUndo(
            record.SelectionToken,
            record.AppliedRevisionSha256));
        AssertEx.False(store.CanUndo(
            "project/node/scene-3",
            record.AppliedRevisionSha256));
        AssertEx.False(store.CanUndo(
            record.SelectionToken,
            EditorCommandDocumentComposer.Revision(applied + "\n#bgshake")));

        store.Consume(record);
        AssertEx.False(store.CanUndo(
            record.SelectionToken,
            record.AppliedRevisionSha256));
    }
}
