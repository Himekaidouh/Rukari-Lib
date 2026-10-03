using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

internal static class PlayerAdvanceObservationWindowTests
{
    public static void DroppedCompletedWindowStillAdvancesItsCloseWatermark()
    {
        DrainQueue();
        try
        {
            FillQueue();
            long droppedSequence = PlayerAdvanceObservationWindow.Open();
            PlayerAdvanceObservationWindow.Capture("dropped completed window");
            PlayerAdvanceObservationWindow.Close();

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

            AssertEx.Equal(supersededSequence, PlayerAdvanceObservationWindow.ClosedSequenceWatermark);
            AssertEx.Equal(supersededSequence + 1, nextSequence);
            AssertEx.True(PlayerAdvanceObservationWindow.IssuedSequence
                > PlayerAdvanceObservationWindow.ClosedSequenceWatermark);

            PlayerAdvanceObservationWindow.Close();
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
        if (PlayerAdvanceObservationWindow.IssuedSequence
            > PlayerAdvanceObservationWindow.ClosedSequenceWatermark)
            PlayerAdvanceObservationWindow.Close();
        DrainQueue();
    }
}
