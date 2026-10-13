using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The author-facing queue placement parameters: <c>at=&lt;ms&gt;</c> and
/// <c>interrupt=true|false</c>.
/// <para>
/// The hard regression bottom line is that a directive WITHOUT them canonicalizes
/// byte-for-byte exactly as before, so the first fixture here is a round trip that
/// would fail the moment anything is appended unconditionally.
/// </para>
/// </summary>
internal static class SlotActionPlacementTests
{
    private static Result<CanonicalTimelineCommand> Canonicalize(string directive)
    {
        if (directive.StartsWith(
                SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            return new SlotPendingCommandFamilyCompiler().Canonicalize(directive);
        }

        if (directive.StartsWith("#char;", StringComparison.Ordinal))
        {
            return new CharacterTransformCommandFamilyCompiler().Canonicalize(directive);
        }

        if (directive.StartsWith(
                SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            return new SceneCameraCommandFamilyCompiler().Canonicalize(directive);
        }

        return new CharacterPresetCommandFamilyCompiler().Canonicalize(directive);
    }

    private static string CanonicalText(string directive)
    {
        Result<CanonicalTimelineCommand> result = Canonicalize(directive);
        AssertEx.True(result.Success, $"{directive}: {result.Error}");
        return AssertEx.NotNull(result.Value).Directive;
    }

    private static SlotActionQueuePlan Plan(params string[] directives)
    {
        Result<SlotActionQueuePlan> plan =
            SlotActionQueuePlanner.PlanDirectives(directives);
        AssertEx.True(plan.Success, plan.Error);
        return AssertEx.NotNull(plan.Value);
    }

    public static void CanonicalTextStaysByteIdenticalWhenNoPlacementIsAuthored()
    {
        // Canonical in, identical out: any unconditional append breaks these.
        AssertEx.Equal(
            "#char;3;set;x=10;duration=0;easing=linear",
            CanonicalText("#char;3;set;x=10;duration=0;easing=linear"));
        AssertEx.Equal(
            "#char;3;move;dx=10;duration=800;easing=linear",
            CanonicalText("#char;3;move;dx=10;duration=800;easing=linear"));
        AssertEx.Equal(
            "#camera;move;dx=200;dzoom=0.25;duration=300;easing=easeOut",
            CanonicalText("#camera;move;dx=200;dzoom=0.25;duration=300;easing=easeOut"));
        AssertEx.Equal(
            "#camera;move;scope=background;dx=20;dzoom=0.5;duration=0;easing=linear",
            CanonicalText("#camera;move;scope=background;dx=20;dzoom=0.5;duration=0;easing=linear"));
        AssertEx.Equal(
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right",
            CanonicalText("#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right"));
        AssertEx.Equal(
            "#fx;1;headbutt;back=30;forward=65;duration=900;direction=right",
            CanonicalText("#fx;1;headbutt;back=30;forward=65;duration=900;direction=right"));
    }

    public static void AuthoredOffsetSurvivesCanonicalizationOnEveryFamily()
    {
        AssertEx.Equal(
            "#char;3;set;x=10;duration=600;easing=linear;at=300",
            CanonicalText("#char;3;set;x=10;duration=600;easing=linear;at=300"));
        AssertEx.Equal(
            "#camera;move;dx=200;duration=600;easing=easeOut;at=100",
            CanonicalText("#camera;move;dx=200;duration=600;easing=easeOut;at=100"));
        AssertEx.Equal(
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right;at=200",
            CanonicalText("#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right;at=200"));

        AssertEx.Equal(
            300,
            AssertEx.NotNull(new CharacterTransformDirectiveParser()
                .Parse("#char;3;set;x=10;duration=600;easing=linear;at=300").Value).Placement.AtMilliseconds);
        AssertEx.Equal(
            100,
            AssertEx.NotNull(new SceneCameraDirectiveParser()
                .Parse("#camera;move;dx=200;duration=600;easing=easeOut;at=100").Value).Placement.AtMilliseconds);
        AssertEx.Equal(
            200,
            AssertEx.NotNull(new CharacterPresetDirectiveParser()
                .Parse("#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right;at=200").Value)
                .Placement.AtMilliseconds);
    }

    public static void InterruptSurvivesCanonicalizationAndFalseNormalizesAway()
    {
        AssertEx.Equal(
            "#char;3;move;dx=10;duration=400;easing=linear;interrupt=true",
            CanonicalText("#char;3;move;dx=10;duration=400;easing=linear;interrupt=true"));
        AssertEx.True(AssertEx.NotNull(new CharacterTransformDirectiveParser()
            .Parse("#char;3;move;dx=10;duration=400;easing=linear;interrupt=true").Value)
            .Placement.Interrupt);

        // interrupt=false is the default, so canonical text drops it again — and it
        // stays droppable next to at=, because only interrupt=true conflicts.
        AssertEx.Equal(
            "#char;3;move;dx=10;duration=400;easing=linear",
            CanonicalText("#char;3;move;dx=10;duration=400;easing=linear;interrupt=false"));
        AssertEx.False(AssertEx.NotNull(new CharacterTransformDirectiveParser()
            .Parse("#char;3;move;dx=10;duration=400;easing=linear;interrupt=false").Value)
            .Placement.Interrupt);
        AssertEx.Equal(
            "#char;3;move;dx=10;duration=400;easing=linear;at=150",
            CanonicalText("#char;3;move;dx=10;duration=400;easing=linear;at=150;interrupt=false"));

        // 2026-10-13 contract change: at= and interrupt=true are mutually exclusive.
        // This used to round-trip with both parameters kept.
        Result<CanonicalTimelineCommand> combined = Canonicalize(
            "#char;3;move;dx=10;duration=400;easing=linear;at=150;interrupt=true");
        AssertEx.False(combined.Success, "at= with interrupt=true must fail closed.");
        AssertEx.True(
            combined.Error.Contains(SlotActionPlacementParser.AtKey, StringComparison.Ordinal)
            && combined.Error.Contains(SlotActionPlacementParser.InterruptKey, StringComparison.Ordinal),
            combined.Error);
    }

    public static void OffsetAndInterruptTogetherFailClosedOnEveryFamily()
    {
        string[] bodies =
        {
            "#char;3;set;x=10;duration=600;easing=linear",
            "#camera;move;dx=200;duration=600;easing=easeOut",
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right"
        };
        foreach (string body in bodies)
        {
            // Both authored orders must fail; the preset parser follows the author's
            // order, the property-table parsers follow a fixed key order.
            foreach (string pair in new[] { "at=300;interrupt=true", "interrupt=true;at=300" })
            {
                Result<CanonicalTimelineCommand> result = Canonicalize($"{body};{pair}");
                AssertEx.False(result.Success, $"{body};{pair} must fail closed.");
                AssertEx.True(
                    result.Error.Contains(SlotActionPlacementParser.AtKey, StringComparison.Ordinal)
                    && result.Error.Contains(SlotActionPlacementParser.InterruptKey, StringComparison.Ordinal),
                    $"{body};{pair}: {result.Error}");
            }
        }

        // The author-facing path reports it on their own line instead of swallowing it.
        EmbeddedAavtExtraction extracted = new EmbeddedAavtDirectiveExtractor().Extract(
            "#aavt;char;3;set;x=10;duration=600;easing=linear;at=300;interrupt=true",
            acceptLegacyCharAlias: false);
        AssertEx.True(extracted.Errors.Count != 0, "the combination must be an extraction error.");
        AssertEx.True(
            extracted.Errors.Any(error =>
                error.Contains(SlotActionPlacementParser.AtKey, StringComparison.Ordinal)
                && error.Contains(SlotActionPlacementParser.InterruptKey, StringComparison.Ordinal)),
            string.Join("|", extracted.Errors));
    }

    public static void OffsetAloneAndInterruptAloneStillPlaceTheSegment()
    {
        SlotActionQueuePlan offset = Plan(
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#char;4;move;dx=10;duration=600;easing=linear;at=300");
        AssertEx.Equal(300d, offset.Segments[1].StartMs);
        AssertEx.True(offset.Segments[1].Placement.HasOffset);
        AssertEx.False(offset.Segments[1].Placement.Interrupt);

        SlotActionQueuePlan interrupt = Plan(
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#char;3;reset;duration=400;easing=linear;interrupt=true");
        AssertEx.Equal(0d, interrupt.Segments[1].StartMs);
        AssertEx.True(interrupt.Segments[1].Placement.Interrupt);
        AssertEx.False(interrupt.Segments[1].Placement.HasOffset);
        AssertEx.Equal(SlotActionSegmentKind.Interrupt, interrupt.Segments[1].Kind);
    }

    public static void InvalidPlacementValuesFailClosedOnEveryFamily()
    {
        string[] bodies =
        {
            "#char;3;set;x=10;duration=600;easing=linear",
            "#camera;move;dx=200;duration=600;easing=easeOut",
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right"
        };
        foreach (string body in bodies)
        {
            foreach (string bad in new[] { "at=abc", "at=-1", "at=1.5", "at=", "interrupt=yes", "interrupt=1" })
            {
                Result<CanonicalTimelineCommand> result = Canonicalize($"{body};{bad}");
                AssertEx.False(result.Success, $"{body};{bad} must fail closed.");
            }
        }

        // The error names the offending property instead of swallowing it.
        Result<CanonicalTimelineCommand> at = Canonicalize(
            "#char;3;set;x=10;duration=600;easing=linear;at=abc");
        AssertEx.True(at.Error.Contains("at", StringComparison.Ordinal), at.Error);
        Result<CanonicalTimelineCommand> interrupt = Canonicalize(
            "#char;3;set;x=10;duration=600;easing=linear;interrupt=yes");
        AssertEx.True(interrupt.Error.Contains("interrupt", StringComparison.Ordinal), interrupt.Error);
    }

    public static void PendingSlotDirectivesRejectPlacementInsteadOfIgnoringIt()
    {
        // #charp stages a start state and never occupies the queue, so a placement
        // parameter there would be silently meaningless. It must be refused.
        AssertEx.False(Canonicalize("#charp;3;set;x=10;at=300").Success);
        AssertEx.False(Canonicalize("#charp;3;set;x=10;interrupt=true").Success);
        // The unplaced form still works.
        AssertEx.Equal(
            "#charp;3;set;x=10",
            CanonicalText("#charp;3;set;x=10"));
    }

    public static void DescribeCarriesTheAuthoredOffsetIntoTheSegment()
    {
        SlotActionQueuePlan plan = Plan(
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#char;3;move;dy=50;duration=300;easing=linear;at=300");
        AssertEx.Equal(2, plan.Segments.Count);
        AssertEx.Equal(0d, plan.Segments[0].StartMs);
        AssertEx.True(plan.Segments[0].Placement.IsNone);
        // at= is an offset from the row origin, not from the cursor.
        AssertEx.Equal(300d, plan.Segments[1].StartMs);
        AssertEx.Equal(300, plan.Segments[1].Placement.AtMilliseconds);
        AssertEx.True(plan.Segments[1].Placement.HasOffset);
        AssertEx.True(
            plan.Diagnostics.Any(line => line.StartsWith("overlap;", StringComparison.Ordinal)),
            string.Join("|", plan.Diagnostics));
        // The cursor still moves past the longest end, so a following append waits.
        AssertEx.Equal(600d, plan.TotalMs);
    }

    public static void InterruptPlacesTheSegmentAtTheStartAndTruncatesTheRunningOne()
    {
        SlotActionQueuePlan plan = Plan(
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#char;3;reset;duration=400;easing=linear;interrupt=true");
        AssertEx.Equal(0d, plan.Segments[0].DurationMs);
        AssertEx.Equal(0d, plan.Segments[1].StartMs);
        AssertEx.Equal(SlotActionSegmentKind.Interrupt, plan.Segments[1].Kind);
        AssertEx.True(plan.Segments[1].Placement.Interrupt);
        AssertEx.Equal(400d, plan.TotalMs);
        AssertEx.True(
            plan.Diagnostics.Any(line => line.StartsWith("interrupt;", StringComparison.Ordinal)),
            string.Join("|", plan.Diagnostics));
    }

    public static void AuthorFacingAavtTextCarriesPlacementThroughTheExtractor()
    {
        var extractor = new EmbeddedAavtDirectiveExtractor();
        EmbeddedAavtExtraction character = extractor.Extract(
            "#aavt;char;3;set;x=10;duration=600;easing=linear;at=300",
            acceptLegacyCharAlias: false);
        AssertEx.Equal(0, character.Errors.Count);
        AssertEx.Equal(
            "#char;3;set;x=10;duration=600;easing=linear;at=300",
            character.Commands.Single().CanonicalDirective);

        EmbeddedAavtExtraction camera = extractor.Extract(
            "#aavt;camera;move;dx=200;duration=600;easing=easeOut;at=100",
            acceptLegacyCharAlias: false);
        AssertEx.Equal(0, camera.Errors.Count);
        AssertEx.Equal(
            "#camera;move;dx=200;duration=600;easing=easeOut;at=100",
            camera.Commands.Single().CanonicalDirective);

        EmbeddedAavtExtraction preset = extractor.Extract(
            "#aavt;fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right;interrupt=true",
            acceptLegacyCharAlias: false);
        AssertEx.Equal(0, preset.Errors.Count);
        AssertEx.Equal(
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right;interrupt=true",
            preset.Commands.Single().CanonicalDirective);

        // A malformed placement is reported on the author's own line, not swallowed.
        EmbeddedAavtExtraction bad = extractor.Extract(
            "#aavt;char;3;set;x=10;at=abc",
            acceptLegacyCharAlias: false);
        AssertEx.True(bad.Errors.Count != 0, "at=abc must be reported as an extraction error.");
    }

    public static void CameraAndPresetTracksHonourTheirOwnOffsets()
    {
        SlotActionQueuePlan camera = Plan(
            "#camera;move;dx=200;duration=600;easing=easeOut",
            "#camera;move;scope=background;dx=60;duration=400;easing=easeOut;at=100");
        AssertEx.Equal(100d, camera.Segments[1].StartMs);
        AssertEx.Equal(100, camera.Segments[1].Placement.AtMilliseconds);

        SlotActionQueuePlan preset = Plan(
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#fx;3;sway;amplitude=25;frequency=2;cycles=2;direction=right;at=200");
        // The preset owns its own track, so its offset does not wait for the move.
        AssertEx.Equal("char:3", preset.Segments[0].SlotKey);
        AssertEx.Equal("preset:3", preset.Segments[1].SlotKey);
        AssertEx.Equal(200d, preset.Segments[1].StartMs);
        AssertEx.Equal(1200d, preset.TotalMs);
    }
}
