using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Per-slot queue semantics for one playback row: append by default, explicit
/// offset or takeover when the author asks, and a bounded tail that must be
/// fast-forwarded instead of dropped.
/// </summary>
internal static class SlotActionQueuePlannerTests
{
    private static SlotActionQueuePlan Plan(
        SlotActionInput[] inputs,
        double budgetMilliseconds = SlotActionQueuePlanner.DefaultBudgetMilliseconds)
    {
        Result<SlotActionQueuePlan> plan = SlotActionQueuePlanner.Plan(inputs, budgetMilliseconds);
        AssertEx.True(plan.Success, plan.Error);
        return AssertEx.NotNull(plan.Value);
    }

    private static void Near(double expected, double actual, string? message = null)
    {
        AssertEx.True(
            Math.Abs(expected - actual) < 0.001,
            message ?? $"Expected <{expected}> but found <{actual}>.");
    }

    public static void SingleActionKeepsItsOwnDuration()
    {
        SlotActionQueuePlan plan = Plan(
            new[] { new SlotActionInput(0, "#char;3;set;x=10;duration=600", "char:3", 600) });
        AssertEx.Equal(1, plan.Segments.Count);
        Near(0d, plan.Segments[0].StartMs);
        Near(600d, plan.Segments[0].DurationMs);
        Near(600d, plan.TotalMs);
        AssertEx.False(plan.OverBudget);
        AssertEx.Equal(0, plan.Diagnostics.Count);
        AssertEx.Equal(SlotActionSegmentKind.Animate, plan.Segments[0].Kind);
    }

    public static void SameSlotActionsQueueInsteadOfOverwriting()
    {
        SlotActionQueuePlan plan = Plan(new[]
        {
            new SlotActionInput(0, "#char;3;set;x=-748;duration=600", "char:3", 600),
            new SlotActionInput(1, "#char;3;set;x=40;rotation=-77;duration=600", "char:3", 600)
        });
        AssertEx.Equal(2, plan.Segments.Count);
        Near(0d, plan.Segments[0].StartMs);
        // The second action starts where the first one ends: 1 -> 2 -> 3 -> 4, never 1 -> 4.
        Near(600d, plan.Segments[1].StartMs);
        Near(1200d, plan.TotalMs);
        AssertEx.Equal(0, plan.Diagnostics.Count);
    }

    public static void DifferentSlotsRunInParallel()
    {
        SlotActionQueuePlan plan = Plan(new[]
        {
            new SlotActionInput(0, "#char;3;set;x=10;duration=600", "char:3", 600),
            new SlotActionInput(1, "#char;4;set;x=20;duration=400", "char:4", 400),
            new SlotActionInput(2, "#camera;1;zoom;duration=800", "camera:1", 800)
        });
        AssertEx.Equal(3, plan.Segments.Count);
        foreach (SlotActionSegment segment in plan.Segments)
        {
            Near(0d, segment.StartMs);
        }

        Near(800d, plan.TotalMs);
    }

    public static void InstantCommitDoesNotConsumeTimeButKeepsOrder()
    {
        SlotActionQueuePlan plan = Plan(new[]
        {
            new SlotActionInput(0, "#char;3;set;x=10;duration=0", "char:3", 0),
            new SlotActionInput(1, "#char;3;move;dx=100;duration=600", "char:3", 600),
            new SlotActionInput(2, "#char;3;reset;duration=0", "char:3", 0)
        });
        AssertEx.Equal(SlotActionSegmentKind.Instant, plan.Segments[0].Kind);
        Near(0d, plan.Segments[1].StartMs);
        Near(600d, plan.TotalMs);
        // The trailing instant lands at the cursor, not before the animation.
        Near(600d, plan.Segments[2].StartMs);
        Near(0d, plan.Segments[2].DurationMs);
        AssertEx.Equal(SlotActionSegmentKind.Instant, plan.Segments[2].Kind);
    }

    public static void ExplicitOffsetOverlapsAndOnlyMovesTheCursorForward()
    {
        SlotActionQueuePlan plan = Plan(new[]
        {
            new SlotActionInput(0, "#char;3;move;dx=100;duration=600", "char:3", 600),
            new SlotActionInput(1, "#char;3;move;dy=50;duration=300", "char:3", 300, AtMs: 300),
            new SlotActionInput(2, "#char;3;move;dx=10;duration=200", "char:3", 200)
        });
        Near(300d, plan.Segments[1].StartMs);
        Near(600d, plan.Segments[1].EndMs);
        AssertEx.True(plan.Diagnostics.Any(line => line.StartsWith("overlap;", StringComparison.Ordinal)));
        // The cursor never moves backwards, so an appended action still starts after 600.
        Near(600d, plan.Segments[2].StartMs);
        Near(800d, plan.TotalMs);
    }

    public static void InterruptTakesOverAndTruncatesTheRunningSegment()
    {
        SlotActionQueuePlan plan = Plan(new[]
        {
            new SlotActionInput(0, "#char;3;move;dx=100;duration=600", "char:3", 600),
            new SlotActionInput(1, "#char;3;reset;duration=400", "char:3", 400, Interrupt: true)
        });
        Near(0d, plan.Segments[0].DurationMs);
        AssertEx.Equal(SlotActionSegmentKind.Interrupt, plan.Segments[1].Kind);
        Near(0d, plan.Segments[1].StartMs);
        Near(400d, plan.TotalMs);
        AssertEx.True(plan.Diagnostics.Any(line => line.StartsWith("interrupt;", StringComparison.Ordinal)));
    }

    public static void OverBudgetTailIsCollapsedForFastForward()
    {
        SlotActionQueuePlan plan = Plan(
            new[]
            {
                new SlotActionInput(0, "#char;3;move;dx=1;duration=600", "char:3", 600),
                new SlotActionInput(1, "#char;3;move;dx=2;duration=600", "char:3", 600),
                new SlotActionInput(2, "#char;3;move;dx=3;duration=600", "char:3", 600)
            },
            budgetMilliseconds: 1000);
        AssertEx.True(plan.OverBudget);
        Near(1000d, plan.TotalMs);
        AssertEx.Equal(SlotActionSegmentKind.Animate, plan.Segments[0].Kind);
        AssertEx.Equal(SlotActionSegmentKind.Collapsed, plan.Segments[1].Kind);
        Near(400d, plan.Segments[1].DurationMs);
        AssertEx.Equal(SlotActionSegmentKind.Collapsed, plan.Segments[2].Kind);
        Near(0d, plan.Segments[2].DurationMs);
        Near(1000d, plan.Segments[2].StartMs);
        AssertEx.True(plan.Diagnostics.Any(line =>
            line.StartsWith("queue-over-budget;", StringComparison.Ordinal)));
    }

    public static void DescribeReadsSlotAndDurationAndRejectsNonSlotDirectives()
    {
        Result<SlotActionInput> transform =
            SlotActionQueuePlanner.Describe(0, "#char;3;move;dx=10;duration=800;easing=linear");
        AssertEx.True(transform.Success, transform.Error);
        SlotActionInput described = AssertEx.NotNull(transform.Value);
        AssertEx.Equal("char:3", described.SlotKey);
        Near(800d, described.DurationMs);

        Result<SlotActionInput> instant =
            SlotActionQueuePlanner.Describe(1, "#char;3;set;x=10;duration=0;easing=linear");
        AssertEx.True(instant.Success, instant.Error);
        Near(0d, AssertEx.NotNull(instant.Value).DurationMs);

        AssertEx.False(SlotActionQueuePlanner.Describe(2, "#wait;100").Success);
        AssertEx.False(SlotActionQueuePlanner.Describe(3, "正文").Success);
    }

    public static void InvalidBudgetsAndDescendingOrderAreRejected()
    {
        AssertEx.False(SlotActionQueuePlanner.Plan(new[]
        {
            new SlotActionInput(1, "#char;3;set;x=1;duration=100", "char:3", 100),
            new SlotActionInput(0, "#char;3;set;x=2;duration=100", "char:3", 100)
        }).Success);
        AssertEx.False(SlotActionQueuePlanner.Plan(Array.Empty<SlotActionInput>(), 0).Success);
        AssertEx.False(SlotActionQueuePlanner.Plan(new[]
        {
            new SlotActionInput(0, "#char;3;set;x=1;duration=100", string.Empty, 100)
        }).Success);
    }

    public static void PlanDirectivesSkipsFamiliesThatAreNotFiniteSlotActions()
    {
        Result<SlotActionQueuePlan> plan = SlotActionQueuePlanner.PlanDirectives(new[]
        {
            "#char;3;move;dx=100;duration=600;easing=linear",
            "#wait;100",
            "#char;3;set;x=40;duration=400;easing=linear"
        });
        AssertEx.True(plan.Success, plan.Error);
        SlotActionQueuePlan queued = AssertEx.NotNull(plan.Value);
        AssertEx.Equal(2, queued.Segments.Count);
        Near(1000d, queued.TotalMs);
        AssertEx.Equal("char:3", queued.Segments[1].SlotKey);
        Near(600d, queued.Segments[1].StartMs);
    }
}
