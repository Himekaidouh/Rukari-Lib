using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Tests;

internal static class ManagedDispatchScheduleTests
{
    public static void DeferredSceneCommandsCannotCrossPreviewAndPlaybackModes()
    {
        var preview = PlayerRuntimeContextSnapshot.FromPreviewMode(true);
        var playback = PlayerRuntimeContextSnapshot.FromPreviewMode(false);
        AssertEx.True(ManagedDispatchContextPolicy.CanDispatch(PlayerRuntimeMode.EditorPreview, preview));
        AssertEx.True(ManagedDispatchContextPolicy.CanDispatch(PlayerRuntimeMode.Playback, playback));
        AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(PlayerRuntimeMode.EditorPreview, playback));
        AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(PlayerRuntimeMode.Playback, preview));
    }

    public static void UnavailableOrInconsistentPlayersNeverConsumeQueuedSceneCommands()
    {
        foreach (var origin in new[] { PlayerRuntimeMode.EditorPreview, PlayerRuntimeMode.Playback })
        {
            AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(origin, null));
            AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(origin, PlayerRuntimeContextSnapshot.Unavailable));
            AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(origin, new(false, origin == PlayerRuntimeMode.EditorPreview, origin)));
            AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(origin, new(true, origin != PlayerRuntimeMode.EditorPreview, origin)));
        }
        AssertEx.False(ManagedDispatchContextPolicy.CanDispatch(PlayerRuntimeMode.Unavailable,
            PlayerRuntimeContextSnapshot.FromPreviewMode(false)));
    }

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
