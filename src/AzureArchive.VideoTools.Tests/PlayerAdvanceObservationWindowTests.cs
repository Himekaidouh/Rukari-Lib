using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

internal static class PlayerAdvanceObservationWindowTests
{
    public static void LifecycleSnapshotTracksActualOpenCompletedAndUnpairedClose()
    {
        CloseAndDrainQueue();
        try
        {
            AdvanceWindowLifecycleSnapshot before = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(0L, before.ActiveWindowSequence);

            long openedSequence = PlayerAdvanceObservationWindow.Open();
            AdvanceWindowLifecycleSnapshot opened = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(openedSequence, opened.ActiveWindowSequence);
            AssertEx.Equal(openedSequence, opened.IssuedWindowSequence);
            AssertEx.Equal(before.ClosedWindowWatermark, opened.ClosedWindowWatermark);

            PlayerAdvanceObservationWindow.Close();
            AdvanceWindowLifecycleSnapshot completed = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(0L, completed.ActiveWindowSequence);
            AssertEx.Equal(openedSequence, completed.IssuedWindowSequence);
            AssertEx.Equal(openedSequence, completed.ClosedWindowWatermark);

            PlayerAdvanceObservationWindow.Close();
            AdvanceWindowLifecycleSnapshot unpaired = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(0L, unpaired.ActiveWindowSequence);
            AssertEx.Equal(openedSequence + 1, unpaired.IssuedWindowSequence);
            AssertEx.Equal(unpaired.IssuedWindowSequence, unpaired.ClosedWindowWatermark);
        }
        finally
        {
            CloseAndDrainQueue();
        }
    }

    public static void LifecycleSnapshotSeparatesOverlappingActiveWindowFromPriorClose()
    {
        CloseAndDrainQueue();
        try
        {
            long priorSequence = PlayerAdvanceObservationWindow.Open();
            long nextSequence = PlayerAdvanceObservationWindow.Open();
            AdvanceWindowLifecycleSnapshot snapshot = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();

            AssertEx.Equal(nextSequence, snapshot.ActiveWindowSequence);
            AssertEx.Equal(nextSequence, snapshot.IssuedWindowSequence);
            AssertEx.Equal(priorSequence, snapshot.ClosedWindowWatermark);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? prior));
            AssertEx.Equal(priorSequence, AssertEx.NotNull(prior).Sequence);
            AssertEx.Equal(AdvanceWindowCloseReason.ReplacedByOverlappingPrefix, prior!.CloseReason);
        }
        finally
        {
            CloseAndDrainQueue();
        }
    }

    public static void DroppedCompletedWindowStillAdvancesItsCloseWatermark()
    {
        DrainQueue();
        try
        {
            FillQueue();
            long droppedSequence = PlayerAdvanceObservationWindow.Open();
            PlayerAdvanceObservationWindow.Capture("dropped completed window");
            PlayerAdvanceObservationWindow.Close();

            AdvanceWindowLifecycleSnapshot snapshot = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(0L, snapshot.ActiveWindowSequence);
            AssertEx.Equal(droppedSequence, snapshot.IssuedWindowSequence);
            AssertEx.Equal(droppedSequence, snapshot.ClosedWindowWatermark);
            AssertEx.Equal(droppedSequence, PlayerAdvanceObservationWindow.ClosedSequenceWatermark);
            AssertEx.Equal(1, PlayerAdvanceObservationWindow.TakeDroppedWindowCount());
            AssertEx.Equal(512, DrainQueue());
        }
        finally
        {
            CloseAndDrainQueue();
        }
    }

    public static void DroppedSupersededWindowClosesBeforeTheNextWindowOpens()
    {
        DrainQueue();
        try
        {
            FillQueue();
            long supersededSequence = PlayerAdvanceObservationWindow.Open();
            PlayerAdvanceObservationWindow.Capture("dropped superseded window");
            long nextSequence = PlayerAdvanceObservationWindow.Open();

            AdvanceWindowLifecycleSnapshot opened = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(nextSequence, opened.ActiveWindowSequence);
            AssertEx.Equal(nextSequence, opened.IssuedWindowSequence);
            AssertEx.Equal(supersededSequence, opened.ClosedWindowWatermark);
            AssertEx.Equal(supersededSequence, PlayerAdvanceObservationWindow.ClosedSequenceWatermark);
            AssertEx.Equal(supersededSequence + 1, nextSequence);
            AssertEx.True(PlayerAdvanceObservationWindow.IssuedSequence
                > PlayerAdvanceObservationWindow.ClosedSequenceWatermark);

            PlayerAdvanceObservationWindow.Close();
            AssertEx.Equal(0L, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot().ActiveWindowSequence);
            AssertEx.Equal(nextSequence, PlayerAdvanceObservationWindow.ClosedSequenceWatermark);
            AssertEx.Equal(2, PlayerAdvanceObservationWindow.TakeDroppedWindowCount());
            AssertEx.Equal(512, DrainQueue());
        }
        finally
        {
            CloseAndDrainQueue();
        }
    }

    public static void DroppedPostfixWithoutPrefixCannotLookLikeAnOpenLogWindow()
    {
        DrainQueue();
        try
        {
            FillQueue();
            PlayerAdvanceObservationWindow.Close();
            AdvanceWindowLifecycleSnapshot snapshot = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            AssertEx.Equal(0L, snapshot.ActiveWindowSequence);
            AssertEx.Equal(snapshot.IssuedWindowSequence, snapshot.ClosedWindowWatermark);
            long issued = PlayerAdvanceObservationWindow.IssuedSequence;
            long closed = PlayerAdvanceObservationWindow.ClosedSequenceWatermark;
            AssertEx.Equal(issued, closed);
            AssertEx.Equal(1, PlayerAdvanceObservationWindow.TakeDroppedWindowCount());

            var correlation = new ManagedSelectionLogCorrelation();
            correlation.Begin(0, 5, 1, issued, closed,
                new EditorPreviewSceneAddress(
                    new string('a', 24), "00112233-4455-6677-8899-aabbccddeeff", 0,
                    new string('b', 24)), 1);
            AssertEx.False(correlation.TryObserveFirst(6,
                CommandIdentity.CompiledScript("log after a dropped close"), 1,
                issued, closed, out string error));
            AssertEx.Equal("log-outside-data-list-open-window", error);
            AssertEx.True(correlation.Pending == null);
            AssertEx.True(correlation.DeferredPending == null);
            AssertEx.Equal(512, DrainQueue());
        }
        finally
        {
            CloseAndDrainQueue();
        }
    }

    private static void FillQueue()
    {
        for (int index = 0; index < 512; index++)
        {
            PlayerAdvanceObservationWindow.Open();
            PlayerAdvanceObservationWindow.Close();
        }
    }

    private static int DrainQueue()
    {
        int drained = 0;
        while (PlayerAdvanceObservationWindow.TryDequeue(out _)) drained++;
        PlayerAdvanceObservationWindow.TakeDroppedWindowCount();
        return drained;
    }

    private static void CloseAndDrainQueue()
    {
        if (PlayerAdvanceObservationWindow.ReadLifecycleSnapshot().ActiveWindowSequence > 0)
            PlayerAdvanceObservationWindow.Close();
        DrainQueue();
    }
}
