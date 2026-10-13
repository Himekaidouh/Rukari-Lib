using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The editor preview takes the SAME deferral decision as playback
/// (<see cref="SlotActionQueueDeferral"/>). These fixtures pin that decision from the
/// preview side, so "what the author sees in the editor" and "what playback does"
/// cannot drift apart: a preview that queues nothing must stay byte-for-byte the
/// behaviour it had before the queue existed.
/// </summary>
internal static class SlotActionPreviewQueueTests
{
    private const string First = "#char;3;set;x=-748.283;y=-60.672;duration=600;easing=easeInOut";
    private const string Second = "#char;3;set;x=40.448;y=-60.672;rotation=-77;duration=600;easing=easeInOut";

    public static void PreviewDefersTheSecondSameSlotSegmentAndKeepsTheFirstInFrame()
    {
        SlotActionDeferralDecision decision =
            SlotActionQueueDeferral.Decide(new[] { First, Second });

        // Same shape the playback batch path takes: index 0 lands with the dispatch,
        // index 1 waits for the first one to end.
        AssertEx.Equal(1, decision.SameFrameIndices.Count);
        AssertEx.Equal(0, decision.SameFrameIndices[0]);
        AssertEx.True(decision.HasDeferredSegments);
        AssertEx.True(decision.DeferredIndices.Contains(1));
        AssertEx.False(decision.DeferredIndices.Contains(0));
        AssertEx.Equal(string.Empty, decision.Error);
        SlotActionQueuePlan plan = AssertEx.NotNull(decision.Plan);
        AssertEx.Equal(2, plan.Segments.Count);
        AssertEx.True(Math.Abs(plan.Segments[1].StartMs - 600d) < 0.001);
        AssertEx.True(Math.Abs(plan.TotalMs - 1200d) < 0.001);

        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(77, plan, 0d);
        AssertEx.Equal(1, queue.PendingCount);
        AssertEx.Equal(0, queue.TakeDue(0.5).Count);
        IReadOnlyList<SlotActionQueuedSegment> due = queue.TakeDue(0.6);
        AssertEx.Equal(1, due.Count);
        AssertEx.Equal(1, due[0].Segment.DirectiveIndex);
        AssertEx.Equal(77L, due[0].Sequence);
    }

    public static void PreviewWithASingleActionQueuesNothingAndKeepsItsHold()
    {
        SlotActionDeferralDecision decision =
            SlotActionQueueDeferral.Decide(new[] { First });

        // No plan at all: the preview keeps dispatching in frame and the hold keeps the
        // duration it had before the slot queue existed.
        AssertEx.True(decision.Plan == null);
        AssertEx.Equal(string.Empty, decision.Error);
        AssertEx.False(decision.HasDeferredSegments);
        AssertEx.Equal(1, decision.SameFrameIndices.Count);
        AssertEx.Equal(0, decision.SameFrameIndices[0]);

        var queue = new SlotActionQueueRuntime();
        AssertEx.True(queue.IsEmpty);
        AssertEx.Equal(0, queue.PendingCount);
    }

    public static void PreviewWithDifferentSlotsRunsEverythingInFrame()
    {
        SlotActionDeferralDecision decision = SlotActionQueueDeferral.Decide(new[]
        {
            "#char;3;move;dx=10;duration=600;easing=linear",
            "#char;4;move;dx=20;duration=400;easing=linear",
            "#camera;set;zoom=1.2;duration=800;easing=linear"
        });

        AssertEx.False(decision.HasDeferredSegments);
        AssertEx.Equal(3, decision.SameFrameIndices.Count);
        SlotActionQueuePlan plan = AssertEx.NotNull(decision.Plan);
        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(11, plan, 0d);
        // Nothing is ever queued for parallel slots, so the preview dispatches them all
        // exactly as it did before.
        AssertEx.True(queue.IsEmpty);
    }

    public static void PreviewIgnoresDirectivesTheQueueDoesNotSchedule()
    {
        SlotActionDeferralDecision decision = SlotActionQueueDeferral.Decide(new[]
        {
            First,
            "#wait;100",
            Second
        });

        // The non-slot directive stays outside the queue but must not disturb the
        // authored order of the ones that are scheduled.
        AssertEx.True(decision.HasDeferredSegments);
        AssertEx.Equal(2, decision.Plan!.Segments.Count);
        AssertEx.True(decision.DeferredIndices.Contains(2));
        AssertEx.True(decision.SameFrameIndices.Contains(0));
        AssertEx.True(decision.SameFrameIndices.Contains(1));
        AssertEx.Equal(string.Empty, decision.Error);
    }
}
