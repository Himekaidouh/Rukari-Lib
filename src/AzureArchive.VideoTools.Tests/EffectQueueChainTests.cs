using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The 「串成一条」 mode: one row, all layers read as a single queue (2026-10-13).
///
/// <para>
/// These tests exist because the mode is a promise about timing that the author cannot check by reading one window:
/// the offsets it writes decide when every <em>other</em> layer's effects start, and a wrong joint is only visible in
/// playback. So each case is checked twice — against the offsets this file wrote, and against the plan the player
/// builds from the row it would save.
/// </para>
/// </summary>
internal static class EffectQueueChainTests
{
    private const string CharacterFirst = "#aavt;char;3;move;dx=100;duration=600";
    private const string CharacterSecond = "#aavt;char;3;move;dy=50;duration=300";
    private const string OtherCharacter = "#aavt;char;4;move;dx=-200;duration=1200";
    private const string Camera = "#aavt;camera;set;zoom=1.2;duration=500";
    private const string LongCamera = "#aavt;camera;set;zoom=1.2;duration=1200";
    private const string Preset = "#aavt;fx;3;squash;frequency=4";
    private const string Pending = "#aavt;charPending;3;set;x=500;flipX=true";
    private const string Spine = "#aavt;spine;3;Eye_Blink;track=20";

    internal static void ACrossLayerJointCarriesTheOffsetAndOneTrackAppendsForFree()
    {
        EffectQueuePlan plan = Plan(true, CharacterFirst, CharacterSecond, Camera);

        AssertEx.Equal(1400d, plan.TotalMs, "The row lasts as long as everything in it added up.");
        AssertEx.Equal(-1, plan.Steps[0].OffsetMs, "The first effect has nothing before it and starts at the origin.");
        AssertEx.Equal(-1, plan.Steps[1].OffsetMs,
            "The second effect of one character follows the first on its own track; an offset would say it twice.");
        AssertEx.Equal(900, plan.Steps[2].OffsetMs, "The camera waits for both character effects.");
        AssertEx.True(plan.Rewritten[2].EndsWith("at=900", StringComparison.Ordinal),
            "Only the joint is written into the text: " + plan.Rewritten[2]);
        AssertEx.False(plan.Rewritten[1].Contains("at=", StringComparison.Ordinal),
            "A plain append stays a plain append: " + plan.Rewritten[1]);
        AssertStarts(plan, 0d, 600d, 900d);
    }

    internal static void TheRowOrderDecidesWhichTrackWaits()
    {
        // The same three effects in the opposite authored order: now it is the character who waits for the camera,
        // which is what "共用一条轨道" means — the row's own order is the queue.
        EffectQueuePlan plan = Plan(true, Camera, CharacterFirst, CharacterSecond);

        AssertStarts(plan, 0d, 500d, 1100d);
        AssertEx.Equal(-1, plan.Steps[0].OffsetMs, "The effect the row starts with needs no offset.");
        AssertEx.Equal(500, plan.Steps[1].OffsetMs, "The character waits for the camera in front of it.");
        AssertEx.Equal(-1, plan.Steps[2].OffsetMs, "Its sibling still appends behind it.");
        AssertEx.Equal(1400d, plan.TotalMs, "One queue takes the same time in either order.");
    }

    internal static void ATrackThatFallsBehindGetsAnOffsetEvenAfterItsOwnAppend()
    {
        // Character, camera, character: the third effect is on the same track as the first, but the queue says it runs
        // after the camera, so the append it would get by default is not enough and it has to say where it starts.
        EffectQueuePlan plan = Plan(true, CharacterFirst, Camera, CharacterSecond);

        AssertStarts(plan, 0d, 600d, 1100d);
        AssertEx.Equal(1100, plan.Steps[2].OffsetMs, "The last effect names the moment the queue puts it at.");
        AssertEx.Equal(1400d, plan.TotalMs, "Every effect is on the queue, so nothing overlaps.");
    }

    internal static void ParallelModeRemovesThePlacementAndKeepsTheTracksIndependent()
    {
        string[] row = { CharacterFirst, CharacterSecond, Camera };
        EffectQueuePlan serial = Plan(true, row);
        EffectQueuePlan parallel = Plan(false, row);
        EffectQueuePlan stripped = Plan(false, Public(serial.Rewritten.ToArray()));

        foreach (string directive in stripped.Rewritten)
        {
            AssertEx.False(directive.Contains("at=", StringComparison.Ordinal),
                "并行模式把偏移去掉，让每条轨回到自己的追加：" + directive);
        }

        AssertEx.Equal(900d, stripped.TotalMs,
            "Parallel tracks overlap, so the row lasts as long as its longest track (the two character effects).");
        AssertEx.Equal(1400d, serial.TotalMs, "The same row read as one queue is longer, not shorter.");
        AssertEx.True(stripped.Line.Contains("各层并行", StringComparison.Ordinal),
            "The line says which of the two modes was written: " + stripped.Line);
        AssertStarts(stripped, 0d, 600d, 0d);
    }

    internal static void PlanningTheRowItJustWroteChangesNothing()
    {
        EffectQueuePlan first = Plan(true, CharacterFirst, Camera, OtherCharacter);
        EffectQueuePlan second = Plan(true, Public(first.Rewritten.ToArray()));

        for (int index = 0; index < first.Rewritten.Count; index++)
        {
            AssertEx.Equal(first.Rewritten[index], second.Rewritten[index],
                $"Applying the queue twice must not move anything (row {index}).");
            AssertEx.Equal(first.Steps[index].OffsetMs, second.Steps[index].OffsetMs,
                $"The second read agrees about where effect {index} starts.");
        }

        AssertEx.Equal(first.TotalMs, second.TotalMs, "And the row is the same length both times.");
        AssertEx.False(first.Note.Contains("原有的 at=", StringComparison.Ordinal),
            "A row this mode wrote has nothing stale in it: " + first.Note);
    }

    internal static void APendingStartStateStaysOutOfTheQueueAndTheChainJumpsOverIt()
    {
        // A pending start state stages a beginning: its grammar has no at=, and the player commits it in the frame the
        // row starts in. So the effect behind it on that track must jump over the gap itself.
        EffectQueuePlan plan = Plan(true, Camera, Pending, CharacterFirst);

        AssertEx.True(plan.Success, plan.Error);
        AssertEx.False(plan.Rewritten[1].Contains("at=", StringComparison.Ordinal),
            "The pending family cannot carry a placement, so none is written: " + plan.Rewritten[1]);
        AssertEx.Equal(500, plan.Steps[2].OffsetMs, "The character move jumps over the camera that runs first.");
        AssertEx.Equal(1100d, plan.TotalMs, "The pending stages nothing time-wise, so the row is camera plus move.");
        AssertEx.True(plan.Note.Contains("起点预置", StringComparison.Ordinal),
            "The author is told that one segment is not queued: " + plan.Note);
        AssertStarts(plan, 0d, 0d, 500d);
    }

    internal static void AFamilyOutsideTheQueueIsNamedAndLeftAlone()
    {
        EffectQueuePlan plan = Plan(true, CharacterFirst, Spine, Camera);

        AssertEx.True(plan.Rewritten[1].StartsWith("#spine;", StringComparison.Ordinal),
            "The overlay keeps its own canonical text: " + plan.Rewritten[1]);
        AssertEx.False(plan.Rewritten[1].Contains("at=", StringComparison.Ordinal),
            "Nothing writes a queue placement into a family that cannot read one.");
        AssertEx.Equal(1100d, plan.TotalMs, "The overlay is outside the action hold, so it does not lengthen the row.");
        AssertEx.Equal(600, plan.Steps[2].OffsetMs, "The camera still waits for the character move.");
        AssertEx.True(plan.Note.Contains("spine", StringComparison.Ordinal),
            "The note names what stayed outside the queue: " + plan.Note);
    }

    internal static void AnAuthoredInterruptIsRefusedWithBothWaysOut()
    {
        EffectQueuePlan plan = Plan(true, CharacterFirst + ";interrupt=true", Camera);

        AssertEx.False(plan.Success, "A takeover cannot be queued with the effects it takes over from.");
        AssertEx.True(plan.Error.Contains("interrupt", StringComparison.Ordinal),
            "The refusal names the parameter: " + plan.Error);
        AssertEx.True(plan.Error.Contains("各层并行", StringComparison.Ordinal),
            "and the way out of it: " + plan.Error);
    }

    internal static void ALongChainIsWrittenAndSaysTheTailWillCollapse()
    {
        // The player collapses whatever runs past its budget and applies that effect's end state instead of animating
        // it. That is a supported outcome, not a failure — but it has to be visible before the author plays the row.
        EffectQueuePlan plan = Plan(true, OtherCharacter, LongCamera, OtherCharacter);

        AssertEx.True(plan.Success, plan.Error);
        AssertEx.True(plan.OverBudget, "3.6 s of queue is past the player's 3 s budget.");
        AssertEx.Equal(3000d, plan.TotalMs, "The plan reports what the player will really spend, collapse included.");
        AssertEx.True(plan.Line.Contains("跳到结束状态", StringComparison.Ordinal),
            "The line says the tail will not animate: " + plan.Line);
    }

    internal static void OnlyTheFamiliesThatParseThePlacementCanCarryIt()
    {
        AssertEx.True(EffectQueueChain.SupportsOffset("#char;3;move;dx=10;duration=600"),
            "The character transform reads at=.");
        AssertEx.True(EffectQueueChain.SupportsOffset("#camera;set;zoom=1.2;duration=500"),
            "The camera reads at=.");
        AssertEx.True(EffectQueueChain.SupportsOffset("#fx;3;squash;frequency=4"),
            "The transient preset reads at=.");
        AssertEx.False(EffectQueueChain.SupportsOffset("#charp;3;set;x=10"),
            "The pending start state rejects at=, so it must never be written there.");
        AssertEx.False(EffectQueueChain.SupportsOffset("#spine;3;play;track=20"),
            "The spine overlay is outside the queue and has no placement.");
    }

    internal static void WritingAnOffsetReplacesTheOneAlreadyThere()
    {
        const string authored = "#char;3;move;dx=10;duration=600;at=300";
        string moved = EffectQueueChain.WithOffset(authored, 900);
        string appended = EffectQueueChain.WithoutOffset(moved);

        AssertEx.Equal("#char;3;move;dx=10;duration=600;at=900", moved,
            "One line never carries two offsets.");
        AssertEx.Equal("#char;3;move;dx=10;duration=600", appended,
            "Removing the offset leaves the directive the author wrote.");
        AssertEx.Equal(1, CountOf(moved, "at="), "The offset appears exactly once.");
    }

    /// <summary>The row the player would read, and where each of its effects really starts.</summary>
    private static void AssertStarts(EffectQueuePlan plan, params double[] expected)
    {
        AssertEx.True(plan.Success, plan.Error);
        var planned = SlotActionQueuePlanner.PlanDirectives(plan.Rewritten);
        AssertEx.True(planned.Success, planned.Error);
        SlotActionQueuePlan queued = AssertEx.NotNull(planned.Value, "The rewritten row must plan.");
        AssertEx.Equal(expected.Length, queued.Segments.Count, "Every queueable effect is placed.");
        foreach (SlotActionSegment segment in queued.Segments)
        {
            AssertEx.True(segment.DirectiveIndex >= 0 && segment.DirectiveIndex < expected.Length,
                "A segment points at the directive it came from.");
            AssertEx.Equal(
                expected[segment.DirectiveIndex],
                segment.StartMs,
                $"Effect {segment.DirectiveIndex} starts where the queue said.");
        }
    }

    private static int CountOf(string text, string token)
    {
        int count = 0;
        int offset = text.IndexOf(token, StringComparison.Ordinal);
        while (offset >= 0)
        {
            count++;
            offset = text.IndexOf(token, offset + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static EffectQueuePlan Plan(bool serial, params string[] publicDirectives) =>
        EffectQueueChain.Plan(Row(publicDirectives), serial);

    /// <summary>The public form of canonical directives, which is what a document holds.</summary>
    private static string[] Public(IReadOnlyList<string> canonicalDirectives)
    {
        var publicDirectives = new string[canonicalDirectives.Count];
        for (int index = 0; index < canonicalDirectives.Count; index++)
            publicDirectives[index] = EditorCommandDocumentComposer.ToPublicDirective(canonicalDirectives[index]);
        return publicDirectives;
    }

    private static IReadOnlyList<EmbeddedAavtCommand> Row(params string[] publicDirectives)
    {
        var commands = new List<EmbeddedAavtCommand>(publicDirectives.Length);
        var extractor = new EmbeddedAavtDirectiveExtractor();
        foreach (string directive in publicDirectives)
        {
            EmbeddedAavtExtraction extraction = extractor.Extract(directive);
            AssertEx.Equal(0, extraction.Errors.Count,
                $"'{directive}' must parse: {string.Join(" | ", extraction.Errors)}");
            AssertEx.Equal(1, extraction.Commands.Count, $"'{directive}' must yield exactly one command.");
            commands.Add(extraction.Commands[0]);
        }

        return commands;
    }
}
