using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class ActionTimelineTests
{
    public static void ParallelActionsUseLongestActualDeadline()
    {
        var gate = new ActionTimelineCompletionGate();
        gate.Arm(11, 100);
        AssertEx.True(gate.BeginDelivery(11, 103));
        AssertEx.True(gate.AddAction(11, 103, 1));
        AssertEx.True(gate.AddAction(11, 103.25, 2));
        gate.Seal(11);
        AssertEx.Equal(105.25, gate.DueAt);
        AssertEx.Equal(ActionTimelineStatus.Running, gate.Tick(105.24, false));
        AssertEx.Equal(ActionTimelineStatus.Completed, gate.Tick(105.25, false));
        AssertEx.False(gate.BeginDelivery(11, 106));
    }

    public static void PersistentLoopsAndInitialPositionDoNotAddWait()
    {
        var plan = ActionTimelineTiming.ParallelDuration(new[]
        {
            "#char;3;set;startX=-900;startY=50;duration=600;easing=easeInOut",
            "#fx;3;sway;amplitude=25;frequency=3;cycles=0;direction=right"
        });
        AssertEx.True(plan.Success, plan.Error);
        AssertEx.Equal(0d, plan.Value);
        var gate = new ActionTimelineCompletionGate();
        gate.Arm(12, 100);
        AssertEx.Equal(ActionTimelineStatus.AwaitingDispatch, gate.Tick(100.5, false));
        AssertEx.True(gate.BeginDelivery(12, 101));
        gate.Seal(12);
        AssertEx.Equal(ActionTimelineStatus.Completed, gate.Tick(101, false));
    }

    public static void FiniteActionsIncludeCyclesHeadbuttCameraAndMovement()
    {
        var plan = ActionTimelineTiming.ParallelDuration(new[]
        {
            "#char;3;set;startX=-900;x=400;duration=600;easing=linear",
            "#camera;set;zoom=1.2;duration=900;easing=linear",
            "#fx;3;headbutt;back=30;forward=65;duration=1200;direction=left",
            "#fx;4;spin;frequency=2;cycles=4;direction=right",
            "#fx;5;squash;amplitude=0.3;frequency=4;cycles=5"
        });
        AssertEx.True(plan.Success, plan.Error);
        AssertEx.Equal(2d, plan.Value);
        var flip = ActionTimelineTiming.ParallelDuration(new[] { "#char;3;set;flipX=true;duration=4000;easing=linear" });
        AssertEx.True(flip.Success, flip.Error);
        AssertEx.Equal(4d, flip.Value);
    }

    public static void StaleAndDuplicateDeliveriesCannotExtendCurrentAction()
    {
        var gate = new ActionTimelineCompletionGate();
        gate.Arm(13, 0);
        AssertEx.False(gate.BeginDelivery(12, 0));
        AssertEx.True(gate.BeginDelivery(13, 1));
        AssertEx.False(gate.BeginDelivery(13, 1));
        AssertEx.False(gate.AddAction(12, 1, 50));
        AssertEx.True(gate.AddAction(13, 1, 2));
        gate.Seal(13);
        AssertEx.False(gate.AddAction(13, 2, 50));
        gate.Arm(14, 2);
        gate.Seal(13);
        AssertEx.Equal(ActionTimelineStatus.AwaitingDispatch, gate.Tick(3, false));
        AssertEx.Equal(14L, gate.Sequence);
    }

    public static void DispatchAndCompletionFailuresHaveBoundedTimeouts()
    {
        var gate = new ActionTimelineCompletionGate();
        gate.Arm(15, 0);
        AssertEx.Equal(ActionTimelineStatus.DispatchTimeout, gate.Tick(10, false));
        AssertEx.False(gate.BeginDelivery(15, 10));
        gate.Arm(16, 20);
        AssertEx.True(gate.BeginDelivery(16, 21));
        AssertEx.True(gate.AddAction(16, 21, 1));
        gate.Seal(16);
        AssertEx.Equal(ActionTimelineStatus.Running, gate.Tick(22, true));
        AssertEx.Equal(ActionTimelineStatus.CompletionTimeout, gate.Tick(25, true));
        gate.Cancel();
        AssertEx.Equal(ActionTimelineStatus.Cancelled, gate.Tick(100, false));
    }

    public static void RejectsInvalidAndUnboundedDurations()
    {
        AssertEx.False(ActionTimelineTiming.ParallelDuration(new[] { "#fx;3;spin;frequency=0;cycles=4" }).Success);
        var gate = new ActionTimelineCompletionGate();
        gate.Arm(17, 0);
        AssertEx.True(gate.BeginDelivery(17, 0));
        foreach (double bad in new[] { -1, double.NaN, double.PositiveInfinity, 1001 })
            AssertEx.False(gate.AddAction(17, 0, bad));
        gate.Seal(17);
        AssertEx.Equal(ActionTimelineStatus.Completed, gate.Tick(0, false));
    }
}
