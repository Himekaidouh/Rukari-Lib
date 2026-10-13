using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Offline coverage for the slot timeline's timing half: which segments a plan
/// defers, when the queue hands one back, and what a forced finish does to the
/// rest. The planner proves where a segment is placed; only the queue proves when
/// it lands and that it is never dropped.
/// </summary>
internal static class SlotActionQueueRuntimeTests
{
    private const string Move600 = "#char;3;move;dx=100;duration=600;easing=linear";

    private static SlotActionQueuePlan Plan(params string[] directives)
    {
        Result<SlotActionQueuePlan> plan = SlotActionQueuePlanner.PlanDirectives(directives);
        AssertEx.True(plan.Success, plan.Error);
        return AssertEx.NotNull(plan.Value);
    }

    private static double ParallelMilliseconds(params string[] directives)
    {
        Result<double> seconds = ActionTimelineTiming.ParallelDuration(directives);
        AssertEx.True(seconds.Success, seconds.Error);
        return seconds.Value * 1000d;
    }

    private static void Near(double expected, double actual, string? message = null)
    {
        AssertEx.True(
            Math.Abs(expected - actual) < 0.001,
            message ?? $"Expected <{expected}> but found <{actual}>.");
    }

    public static void SingleActionRowKeepsThePreviousHoldDuration()
    {
        string[] directives = { "#char;3;move;dx=10;duration=800;easing=linear" };
        SlotActionQueuePlan plan = Plan(directives);
        AssertEx.Equal(1, plan.Segments.Count);
        Near(0d, plan.Segments[0].StartMs);
        Near(800d, plan.TotalMs);

        // Regression bottom line: with one action per slot the hold is exactly the
        // duration it had before the queue existed, and nothing is ever queued.
        double parallel = ParallelMilliseconds(directives);
        Near(parallel, Math.Max(parallel, plan.TotalMs));
        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(1, plan, 10d);
        AssertEx.True(queue.IsEmpty);
        AssertEx.Equal(0, queue.PendingCount);
    }

    public static void SecondSameSlotActionWaitsForTheFirstOneToEnd()
    {
        string[] directives = { Move600, Move600 };
        SlotActionQueuePlan plan = Plan(directives);
        AssertEx.Equal(2, plan.Segments.Count);
        Near(0d, plan.Segments[0].StartMs);
        Near(600d, plan.Segments[1].StartMs);
        Near(1200d, plan.TotalMs);
        // 1 -> 2 -> 3 -> 4 instead of 1 -> 4: the hold has to cover the whole queue,
        // while the parallel duration still only sees the longest single action.
        double parallel = ParallelMilliseconds(directives);
        Near(600d, parallel);
        AssertEx.True(
            plan.TotalMs > parallel,
            $"The queue total {plan.TotalMs} must extend the parallel duration {parallel}.");

        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(42, plan, 100d);
        AssertEx.Equal(1, queue.PendingCount);
        AssertEx.Equal(0, queue.TakeDue(100.5).Count);

        IReadOnlyList<SlotActionQueuedSegment> due = queue.TakeDue(100.6);
        AssertEx.Equal(1, due.Count);
        AssertEx.Equal(1, due[0].Segment.DirectiveIndex);
        AssertEx.Equal(42L, due[0].Sequence);
        // A segment is handed back once and only once.
        AssertEx.Equal(0, queue.TakeDue(200d).Count);
        AssertEx.True(queue.IsEmpty);
    }

    public static void QueuedSegmentsComeBackInDirectiveOrder()
    {
        SlotActionQueuePlan plan = Plan(
            Move600,
            "#char;3;move;dx=200;duration=600;easing=linear",
            "#char;3;move;dx=300;duration=600;easing=linear");
        AssertEx.Equal(3, plan.Segments.Count);
        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(5, plan, 0d);
        AssertEx.Equal(2, queue.PendingCount);

        IReadOnlyList<SlotActionQueuedSegment> due = queue.TakeDue(5d);
        AssertEx.Equal(2, due.Count);
        AssertEx.Equal(1, due[0].Segment.DirectiveIndex);
        AssertEx.Equal(2, due[1].Segment.DirectiveIndex);
        AssertEx.True(due.All(item => item.Sequence == 5L));
    }

    public static void DifferentSlotsRunInParallelAndQueueNothing()
    {
        SlotActionQueuePlan plan = Plan(
            "#char;3;move;dx=10;duration=600;easing=linear",
            "#char;4;move;dx=20;duration=400;easing=linear",
            "#camera;set;zoom=1.2;duration=800;easing=linear");
        AssertEx.Equal(3, plan.Segments.Count);
        foreach (SlotActionSegment segment in plan.Segments)
        {
            Near(0d, segment.StartMs);
        }

        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(9, plan, 0d);
        AssertEx.True(queue.IsEmpty);
    }

    public static void ForcedFinishReturnsOnlyItsOwnWindowInDirectiveOrder()
    {
        SlotActionQueuePlan plan = Plan(
            Move600,
            "#char;3;move;dx=200;duration=600;easing=linear",
            "#char;3;move;dx=300;duration=600;easing=linear");
        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(7, plan, 0d);
        queue.Enqueue(9, plan, 0d);
        AssertEx.Equal(4, queue.PendingCount);

        IReadOnlyList<SlotActionQueuedSegment> forced = queue.FastForwardRemaining(7);
        AssertEx.Equal(2, forced.Count);
        AssertEx.Equal(1, forced[0].Segment.DirectiveIndex);
        AssertEx.Equal(2, forced[1].Segment.DirectiveIndex);
        AssertEx.True(forced.All(item => item.Sequence == 7L));
        AssertEx.Equal(-1d, queue.ElapsedMilliseconds(7, 1d));
        // A forced finish belongs to one row: another row's queue is never consumed
        // by it, and the endpoints it still owes stay pending.
        AssertEx.Equal(2, queue.PendingCount);
    }

    public static void QueueingNeverShortensTheHoldAndIgnoresForeignDirectives()
    {
        string[] directives = { Move600, "#wait;100" };
        SlotActionQueuePlan plan = Plan(directives);
        AssertEx.Equal(1, plan.Segments.Count);
        double parallel = ParallelMilliseconds(directives);
        Near(600d, parallel);
        // The hold takes max(parallel, queue total): queueing may extend it, never
        // shorten it, so a duration the planner does not schedule is not lost.
        Near(600d, Math.Max(parallel, plan.TotalMs));
    }

    public static void ClearingAndReEnqueueingKeepTheQueueBounded()
    {
        SlotActionQueuePlan plan = Plan(
            Move600,
            "#char;3;move;dx=200;duration=600;easing=linear");
        var queue = new SlotActionQueueRuntime();
        queue.Enqueue(3, plan, 0d);
        queue.Enqueue(5, plan, 0d);
        AssertEx.Equal(2, queue.PendingCount);
        queue.Clear(3);
        AssertEx.Equal(1, queue.PendingCount);
        queue.ClearAll();
        AssertEx.True(queue.IsEmpty);

        // Re-planning one row replaces its queue instead of stacking a second copy.
        queue.Enqueue(3, plan, 0d);
        queue.Enqueue(3, plan, 0d);
        AssertEx.Equal(1, queue.PendingCount);
        Near(0d, queue.ElapsedMilliseconds(3, 0d));
        Near(600d, queue.ElapsedMilliseconds(3, 0.6d));
    }
}
