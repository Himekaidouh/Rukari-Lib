using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Writing one whole resource layer at once: the overlay model's only new write, and the one that lets a window stack
/// two effects inside its own layer. The editor's document model refuses such a text on purpose, so the guarantees it
/// gives elsewhere — one writer per resource, unrelated lines untouched, stale drafts refused — are checked here.
/// </summary>
internal static class EditorCommandLayerReplaceTests
{
    private const string CharacterLine = "#aavt;char;3;set;x=10;duration=0;easing=linear";
    private const string FirstCamera = "#aavt;camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut";
    private const string SecondCamera = "#aavt;camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear";
    private const string BackgroundCamera = "#aavt;camera;set;scope=background;zoom=1.5;duration=250;easing=easeOut";

    private static EditorCommandLayerEdit Replace(
        EditorCommandDocumentComposer composer,
        string source,
        string resourceKey,
        params string[] directives)
    {
        Result<EditorCommandLayerEdit> result = composer.PreviewReplaceResource(
            source,
            EditorCommandDocumentComposer.Revision(source),
            resourceKey,
            directives);
        return AssertEx.NotNull(result.Value, result.Error);
    }

    internal static void OneLayerGrowsFromOneLineToTwoAndLeavesEveryOtherLineAlone()
    {
        string source = CharacterLine + "\r\n" + FirstCamera + "\r\n#bgshake";
        var composer = new EditorCommandDocumentComposer();

        EditorCommandLayerEdit edit = Replace(composer, source, CommandResourceIdentity.CameraKey,
            FirstCamera, SecondCamera);

        // What the player will do with the merged text: two camera segments on the one screen track, one after the
        // other. This is the point of the whole write — a row the document model refuses, and the queue plays.
        SlotActionQueuePlan plan = AssertEx.NotNull(SlotActionQueuePlanner.PlanDirectives(new[]
        {
            "#camera;set;x=0;y=0;zoom=1.2;duration=300;easing=easeOut",
            "#camera;set;x=200;y=0;zoom=1.6;duration=400;easing=linear"
        }).Value);
        AssertEx.Equal(2, plan.Segments.Count, "Two directives in one layer are two queued segments.");
        AssertEx.Equal(700d, plan.TotalMs, "The second segment runs after the first.");

        AssertEx.Equal(2, edit.CanonicalPublicDirectives.Count, "Both directives of the layer are kept.");
        AssertEx.Equal(FirstCamera, edit.CanonicalPublicDirectives[0], "The first line keeps its authored form.");
        AssertEx.Equal(SecondCamera, edit.CanonicalPublicDirectives[1], "The second line keeps its authored form.");
        AssertEx.True(edit.ReplacedExisting, "The layer had a line to replace.");
        AssertEx.Equal(
            CharacterLine + "\r\n" + FirstCamera + "\r\n" + SecondCamera + "\r\n#bgshake",
            edit.UpdatedText,
            "Only the camera layer changed: the character line and #bgshake keep their bytes and their order.");
        AssertEx.False(
            string.Equals(edit.ExpectedRevisionSha256, edit.ResultRevisionSha256, StringComparison.Ordinal),
            "A write that changed the text reports a new revision.");
    }

    internal static void RemovingALayerTakesEveryOneOfItsLinesWithIt()
    {
        string source = CharacterLine + "\n" + FirstCamera + "\n" + SecondCamera;
        var composer = new EditorCommandDocumentComposer();
        AssertEx.False(composer.Read(source).Success,
            "The document model refuses this very row, which is why the layer write exists.");

        EditorCommandLayerEdit edit = Replace(composer, source, CommandResourceIdentity.CameraKey);

        AssertEx.True(edit.ReplacedExisting, "The layer had lines, and all of them are gone.");
        // The blank tail is what removing a line leaves behind, exactly as PreviewRemove has always left it: the
        // removed line takes its own terminator with it and nothing else is touched.
        AssertEx.Equal(CharacterLine + "\n", edit.UpdatedText, "An empty layer leaves the rest of the document alone.");
        AssertEx.Equal(0, edit.CanonicalPublicDirectives.Count, "The layer is now empty.");
    }

    internal static void AnAbsentLayerIsAppendedWithTheDocumentsOwnNewline()
    {
        const string source = "#wait;500\n#bgshake";
        var composer = new EditorCommandDocumentComposer();

        EditorCommandLayerEdit edit = Replace(composer, source, "preset:3", "#aavt;fx;3;squash;frequency=4");

        AssertEx.False(edit.ReplacedExisting, "The row had no preset layer to replace.");
        AssertEx.Equal(1, edit.CanonicalPublicDirectives.Count, "The new layer holds the one directive asked for.");
        // The compiler fills the preset's defaults in, so the appended line is compared against what was written
        // rather than against what was typed: those are the bytes the next read will hand back.
        AssertEx.Equal("#wait;500\n#bgshake\n" + edit.CanonicalPublicDirectives[0], edit.UpdatedText,
            "A new layer is appended with the document's own line ending.");
        AssertEx.True(edit.CanonicalPublicDirectives[0].StartsWith("#aavt;fx;3;squash;", StringComparison.Ordinal),
            "The preset keeps its family, slot and verb and gains the defaults the compiler fills in.");
    }

    internal static void ADirectiveForAnotherLayerIsRefused()
    {
        string source = CharacterLine + "\n" + FirstCamera;
        var composer = new EditorCommandDocumentComposer();

        Result<EditorCommandLayerEdit> result = composer.PreviewReplaceResource(
            source,
            EditorCommandDocumentComposer.Revision(source),
            CommandResourceIdentity.CameraKey,
            new[] { "#aavt;char;3;move;dx=50;duration=200" });

        AssertEx.False(result.Success, "A camera window may not write a character effect.");
        AssertEx.True(
            result.Error is not null
            && result.Error.Contains("character:3", StringComparison.Ordinal)
            && result.Error.Contains("camera", StringComparison.Ordinal),
            "The refusal names both the layer the directive belongs to and the layer it was written to.");
    }

    internal static void StaleAndMalformedDraftsAreRefused()
    {
        string source = CharacterLine + "\n" + FirstCamera;
        var composer = new EditorCommandDocumentComposer();
        string revision = EditorCommandDocumentComposer.Revision(source);

        AssertEx.False(composer.PreviewReplaceResource(
            source, new string('0', 64), CommandResourceIdentity.CameraKey, new[] { FirstCamera }).Success,
            "A stale revision is refused.");

        AssertEx.False(composer.PreviewReplaceResource(
            source, revision, CommandResourceIdentity.CameraKey,
            new[] { "#aavt;camera;set;zoom=1.2;duration=300\n#aavt;camera;set;zoom=1.3" }).Success,
            "A draft that is two lines is not one directive.");

        AssertEx.False(composer.PreviewReplaceResource(
            source, revision, CommandResourceIdentity.CameraKey, new[] { "这不是指令" }).Success,
            "A draft that is not a resource directive is refused.");

        AssertEx.False(composer.PreviewReplaceResource(
            source, revision, CommandResourceIdentity.CameraKey, new string[] { null! }).Success,
            "A null directive is refused rather than silently dropped.");
    }

    internal static void TwoDirectivesForOneLayerAreAcceptedWhileTheDocumentModelStillRefusesThem()
    {
        string source = CharacterLine + "\n" + FirstCamera;
        var composer = new EditorCommandDocumentComposer();

        EditorCommandLayerEdit edit = Replace(composer, source, CommandResourceIdentity.CameraKey,
            FirstCamera, SecondCamera);

        AssertEx.False(composer.Read(edit.UpdatedText).Success,
            "The picture editor still edits one command per resource; the layer write is how a row gets two.");
        AssertEx.True(composer.Read(source).Success, "One camera directive per row is still an ordinary row.");
    }

    internal static void TheWrittenLayerStaysWhereItsFirstLineWas()
    {
        string source = "#wait;500\r\n" + CharacterLine + "\r\n" + FirstCamera + "\r\n#bgshake";
        var composer = new EditorCommandDocumentComposer();

        EditorCommandLayerEdit edit = Replace(composer, source, CommandResourceIdentity.CameraKey,
            FirstCamera, SecondCamera);

        string[] lines = edit.UpdatedText.Split("\r\n");
        AssertEx.Equal(source.Split("\r\n").Length + 1, lines.Length,
            "The layer grew by one line and nothing else moved.");
        AssertEx.Equal(3, Array.IndexOf(lines, FirstCamera) + 1,
            "The layer's first line is still the third line of the document.");
        AssertEx.Equal(SecondCamera, lines[3], "The second line of the layer follows the first.");
        AssertEx.False(edit.UpdatedText.Replace("\r\n", string.Empty).Contains('\n'),
            "A CRLF document does not gain a bare LF.");
    }

    internal static void TwoLayersAreWrittenInOneDraftByChainingRevisions()
    {
        string source = CharacterLine + "\n" + FirstCamera;
        var composer = new EditorCommandDocumentComposer();

        EditorCommandLayerEdit overall = Replace(composer, source, CommandResourceIdentity.CameraKey,
            FirstCamera, SecondCamera);
        // The second write is planned against the first one's result and revision, which is what a window writing two
        // layers of one row does.
        Result<EditorCommandLayerEdit> background = composer.PreviewReplaceResource(
            overall.UpdatedText,
            overall.ResultRevisionSha256,
            CommandResourceIdentity.BackgroundCameraKey,
            new[] { BackgroundCamera });

        EditorCommandLayerEdit edit = AssertEx.NotNull(background.Value, background.Error);
        AssertEx.False(edit.ReplacedExisting, "The background layer did not exist yet.");
        AssertEx.Equal(4, edit.UpdatedText.Split('\n').Length, "Two camera scopes and the character make three lines.");
        AssertEx.True(edit.UpdatedText.StartsWith(CharacterLine + "\n", StringComparison.Ordinal),
            "The character's line is still the first line of the document.");
        AssertEx.Equal(1, edit.CanonicalPublicDirectives.Count, "The background layer holds the one directive.");
        AssertEx.True(edit.UpdatedText.EndsWith(BackgroundCamera, StringComparison.Ordinal),
            "The new scope's line is the last line of the document.");
    }

    internal static void AnInvalidSourceDocumentIsRefusedBeforeAnythingIsWritten()
    {
        // A row that cannot be parsed cannot be safely rewritten line by line, so it is refused even though the layer
        // being written looks fine on its own.
        const string source = "#aavt;camera;set;scope=background;scope=overall;zoom=2\n" + CharacterLine;
        var composer = new EditorCommandDocumentComposer();

        Result<EditorCommandLayerEdit> result = composer.PreviewReplaceResource(
            source,
            EditorCommandDocumentComposer.Revision(source),
            CommandResourceIdentity.CameraKey,
            new[] { FirstCamera });

        AssertEx.False(result.Success, "A document with an unparsable line is refused.");
        AssertEx.True(result.Error is not null && result.Error.Contains("invalid", StringComparison.Ordinal),
            "The refusal says the document itself is the problem.");
    }
}
