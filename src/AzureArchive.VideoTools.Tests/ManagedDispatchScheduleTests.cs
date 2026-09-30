using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class ManagedDispatchScheduleTests
{
    public static void SceneDispatchWaitsForSameFrameLateUpdate()
    {
        ManagedDispatchSchedule schedule =
            ManagedDispatchSchedule.SameFrameLateUpdate(42);

        AssertEx.Equal(42L, schedule.EarliestUpdate);
        AssertEx.Equal(ManagedDispatchPhase.LateUpdate, schedule.EarliestPhase);
        AssertEx.False(schedule.IsDue(42, ManagedDispatchPhase.Update));
        AssertEx.True(schedule.IsDue(42, ManagedDispatchPhase.LateUpdate));
        AssertEx.True(schedule.IsDue(43, ManagedDispatchPhase.Update));
    }

    public static void NonSceneDispatchKeepsNextUpdateBoundary()
    {
        ManagedDispatchSchedule schedule =
            ManagedDispatchSchedule.NextUpdate(42);

        AssertEx.Equal(43L, schedule.EarliestUpdate);
        AssertEx.Equal(ManagedDispatchPhase.Update, schedule.EarliestPhase);
        AssertEx.False(schedule.IsDue(42, ManagedDispatchPhase.LateUpdate));
        AssertEx.True(schedule.IsDue(43, ManagedDispatchPhase.Update));
    }
}
