using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
using System.Linq;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The row's queue marker: <c>#aavt;queue</c>, the one directive that says how a whole row is laid out (2026-10-13).
///
/// <para>
/// These tests cover the three places the marker has to be true at once: the extractor has to read it and keep it out
/// of the spoken text, the composer has to write and erase exactly that line, and the embedded compile has to lay the
/// row out as one queue — the last one is what the player actually obeys, so it is checked against the planner rather
/// than against the directives alone.
/// </para>
/// </summary>
internal static class EffectQueueMarkerTests
{
    private const string CharacterMove = "#aavt;char;3;move;dx=100;duration=600";
    private const string Camera = "#aavt;camera;set;zoom=1.2;duration=500";
    private const string Marker = "#aavt;queue";

    internal static void TheMarkerIsReadAndKeptOutOfTheSpokenText()
    {
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(
            "正文台词\n" + Marker + "\n" + Camera + "\n" + CharacterMove);

        AssertEx.Equal(0, extraction.Errors.Count,
            "The marker is a directive this build understands: " + string.Join(" | ", extraction.Errors));
        AssertEx.Equal(EffectQueueMode.OneQueue, extraction.QueueMode, "The row says it is one queue.");
        AssertEx.Equal(2, extraction.Commands.Count, "The marker is not an effect: the row still holds two.");
        AssertEx.Equal(3, extraction.RemovedLineCount, "All three directive lines leave the spoken text.");
        AssertEx.Equal("正文台词\n", extraction.SanitizedText, "Only the marker lines were removed.");
    }

    internal static void ARowWithoutTheMarkerKeepsItsOwnTracks()
    {
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(Camera);
        AssertEx.Equal(EffectQueueMode.IndependentTracks, extraction.QueueMode,
            "Nothing in the row asks for a queue, which is what rows did before the marker existed.");
    }

    internal static void AnUnknownValueFailsClosedInsteadOfMeaningSomethingElse()
    {
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract(
            "#aavt;queue;sometimes\n" + Camera);

        AssertEx.Equal(1, extraction.Errors.Count, "A marker this build cannot honour is an error, not a guess.");
        AssertEx.True(extraction.Errors[0].Contains("#aavt;queue", StringComparison.Ordinal),
            "The error names the form to write: " + extraction.Errors[0]);
        AssertEx.Equal(EffectQueueMode.IndependentTracks, extraction.QueueMode,
            "A refused marker does not silently turn the queue on.");
    }

    internal static void TheExplicitSpellingIsTheSameMarker()
    {
        EmbeddedAavtExtraction extraction = new EmbeddedAavtDirectiveExtractor().Extract("#aavt;queue;serial");
        AssertEx.Equal(0, extraction.Errors.Count, "The readable spelling is accepted.");
        AssertEx.Equal(EffectQueueMode.OneQueue, extraction.QueueMode, "and means the same thing.");
    }

    internal static void TheComposerWritesAndErasesExactlyThatLine()
    {
        var composer = new EditorCommandDocumentComposer();
        string source = Camera + "\n#aavt;continue";
        string revision = EditorCommandDocumentComposer.Revision(source);

        EditorCommandQueueEdit added = composer.SetQueueMode(source, revision, EffectQueueMode.OneQueue);
        AssertEx.False(added.AlreadyPresent, "The row did not have the marker.");
        AssertEx.True(added.UpdatedText.EndsWith(Marker, StringComparison.Ordinal),
            "The marker is its own line at the end: " + added.UpdatedText.Replace("\n", "\\n"));
        AssertEx.True(added.UpdatedText.Contains("#aavt;continue", StringComparison.Ordinal),
            "and the row's other marker is left alone.");

        EditorCommandQueueEdit again = composer.SetQueueMode(
            added.UpdatedText,
            added.ResultRevisionSha256,
            EffectQueueMode.OneQueue);
        AssertEx.True(again.AlreadyPresent, "Writing the marker twice is not a second line.");
        AssertEx.Equal(added.UpdatedText, again.UpdatedText, "and does not touch the text.");

        EditorCommandQueueEdit removed = composer.SetQueueMode(
            added.UpdatedText,
            added.ResultRevisionSha256,
            EffectQueueMode.IndependentTracks);
        // Erasing the last line leaves its line break behind — the same thing the continue toggle does, and inert: the
        // extractor reads no directive and no text out of an empty line.
        AssertEx.Equal(source, removed.UpdatedText.TrimEnd('\r', '\n'),
            "Erasing the marker leaves the row itself untouched.");
        AssertEx.Equal(EffectQueueMode.IndependentTracks, new EmbeddedAavtDirectiveExtractor()
            .Extract(removed.UpdatedText).QueueMode, "and the row reads back as independent tracks.");
    }

    internal static void TheCompileLaysAMarkedRowOutAsOneQueue()
    {
        // The marker's whole job: the compile hands the player directives that already say where each effect starts,
        // so the existing queue plays them one after another instead of side by side.
        Result<IReadOnlyList<string>> directives = CompiledDirectivesOf(
            Marker + "\n" + CharacterMove + "\n" + Camera);

        AssertEx.True(directives.Success, directives.Error);
        SlotActionQueuePlan plan = AssertEx.NotNull(
            SlotActionQueuePlanner.PlanDirectives(AssertEx.NotNull(directives.Value)).Value,
            "The compiled row must plan.");
        AssertEx.Equal(1100d, plan.TotalMs, "The camera now waits for the character move: 600 + 500.");
        AssertEx.Equal(2, plan.Segments.Count, "Both effects are queued.");
        AssertEx.Equal(0d, plan.Segments[0].StartMs, "The first effect still starts at the origin.");
        AssertEx.Equal(600d, plan.Segments[1].StartMs, "The second starts where the first ended.");
    }

    internal static void AnUnmarkedRowCompilesToItsOwnDirectives()
    {
        // The other half: no marker means the row is handed through untouched, so nothing about existing content moves.
        Result<IReadOnlyList<string>> directives = CompiledDirectivesOf(CharacterMove + "\n" + Camera);

        AssertEx.True(directives.Success, directives.Error);
        foreach (string directive in AssertEx.NotNull(directives.Value))
        {
            AssertEx.False(directive.Contains("at=", StringComparison.Ordinal),
                "Nothing was added to an unmarked row: " + directive);
        }

        SlotActionQueuePlan plan = AssertEx.NotNull(
            SlotActionQueuePlanner.PlanDirectives(AssertEx.NotNull(directives.Value)).Value,
            "The compiled row must plan.");
        AssertEx.Equal(600d, plan.TotalMs, "The two layers still run side by side.");
    }

    /// <summary>
    /// The directives one row plays. It goes through <see cref="EffectQueueLayout.Applied"/> — the same call the live
    /// capture makes — rather than through the planner directly, because the thing worth pinning is that the marker
    /// reaches the caches playback and the preview read their directives from.
    /// </summary>
    private static Result<IReadOnlyList<string>> CompiledDirectivesOf(string rowText)
    {
        EmbeddedAavtExtraction extracted = new EmbeddedAavtDirectiveExtractor().Extract(rowText);
        AssertEx.Equal(0, extracted.Errors.Count,
            "The row must parse: " + string.Join(" | ", extracted.Errors));
        EmbeddedAavtExtraction laid = EffectQueueLayout.Applied(extracted, out string error);
        AssertEx.Equal(string.Empty, error, "The row must be layoutable.");
        return Result<IReadOnlyList<string>>.Ok(Array.AsReadOnly(
            laid.Commands.Select(command => command.CanonicalDirective).ToArray()));
    }

    internal static void ARowThatCannotBeLaidOutKeepsItsOwnDirectives()
    {
        // A marker never breaks a row: an interrupt= cannot be queued, so the row plays exactly as its directives read
        // and the reason comes back to whoever asked.
        EmbeddedAavtExtraction extracted = new EmbeddedAavtDirectiveExtractor().Extract(
            Marker + "\n#aavt;char;3;move;dx=100;duration=600;interrupt=true");
        EmbeddedAavtExtraction laid = EffectQueueLayout.Applied(extracted, out string error);

        AssertEx.True(error.Length != 0, "The refusal reason is reported.");
        AssertEx.True(error.Contains("interrupt", StringComparison.Ordinal),
            "and names what could not be queued: " + error);
        AssertEx.Equal(EffectQueueMode.OneQueue, laid.QueueMode, "The row still says what it asked for.");
        AssertEx.Equal(
            extracted.Commands[0].CanonicalDirective,
            laid.Commands[0].CanonicalDirective,
            "The effect keeps the directive the author wrote.");
    }
}
