using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Tests;

internal static class PlayerAdvanceObservationWindowTests
{
    public static void TypedParserInputReplacesDiagnosticStringsAndKeepsEmpty()
    {
        CloseAndDrainQueue();
        AssertEx.Equal(0L, PlayerAdvanceObservationWindow.CaptureParsedScript("outside parser"));
        long sequence = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("unrelated diagnostic");
            AssertEx.Equal(sequence, PlayerAdvanceObservationWindow.CaptureParsedScript(string.Empty, 1));
            PlayerAdvanceObservationWindow.Capture("another diagnostic");
            PlayerAdvanceObservationWindow.EndAdvance(sequence, 2);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out var result));
            var window = AssertEx.NotNull(result);
            AssertEx.Equal(1, window.ManagedUnityMessages.Count);
            AssertEx.Equal(string.Empty, window.ManagedUnityMessages.Single());
            AssertEx.Equal(1, window.PlaybackRowIndex);
            AssertEx.False(window.EmbeddedCommandConflict);
        }
        finally { PlayerAdvanceObservationWindow.AbortAdvance(sequence); CloseAndDrainQueue(); }
    }

    public static void ConflictingTypedInputsAndAbortedCallsRemainRejected()
    {
        CloseAndDrainQueue();
        long sequence = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            AssertEx.Equal(sequence, PlayerAdvanceObservationWindow.CaptureParsedScript("first"));
            AssertEx.Equal(0L, PlayerAdvanceObservationWindow.CaptureParsedScript("second"));
            PlayerAdvanceObservationWindow.EndAdvance(sequence, 1);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out var result));
            AssertEx.True(AssertEx.NotNull(result).EmbeddedCommandConflict);
            long next = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.CaptureParsedScript(string.Empty);
            PlayerAdvanceObservationWindow.EndAdvance(next, 2);
            PlayerAdvanceObservationWindow.AbortAdvance(next);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out var aborted));
            AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(aborted).CloseReason);
        }
        finally { PlayerAdvanceObservationWindow.AbortAdvance(sequence); CloseAndDrainQueue(); }
    }

    public static void CompletedWindowRejectedWhenLaterPostfixThrows()
    {
        CloseAndDrainQueue();
        long outer = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("outer compiled identity");
            long nested = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.Capture("nested compiled identity");
            PlayerAdvanceObservationWindow.EndAdvance(nested, 1);
            PlayerAdvanceObservationWindow.EndAdvance(outer, 1);
            // Both calls have ended. A different plugin's later postfix throws,
            // so our finalizer must invalidate queued windows through the marker.
            PlayerAdvanceObservationWindow.AbortAdvance(nested);
            for (int index = 0; index < 2; index++)
            {
                AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? pending));
                AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(pending).CloseReason);
                AssertEx.Equal(outer, pending!.CallChainId);
            }

            long replay = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.Capture("replayed compiled identity");
            PlayerAdvanceObservationWindow.EndAdvance(replay, 1);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? replayed));
            ClosedAdvanceWindow consumed = AssertEx.NotNull(replayed);
            AssertEx.Equal(AdvanceWindowCloseReason.Completed, consumed.CloseReason);
            AssertEx.Equal(replay, consumed.CallChainId);
            PlayerAdvanceObservationWindow.AbortAdvance(replay);
            AssertEx.Equal(AdvanceWindowCloseReason.Completed, consumed.CloseReason);
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            CloseAndDrainQueue();
        }
    }

    public static void SkippedOriginalAfterAdmittedPrefixAbortsWithoutLeakingChain()
    {
        CloseAndDrainQueue();
        AdvanceWindowLifecycleSnapshot before = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
        PlayerAdvanceObservationWindow.EndAdvance(0, -1, originalRan: false);
        AssertEx.Equal(before, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot());
        long admitted = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("compiled partial identity");
            // The exact owned callback used by Postfix handles a later prefix's
            // skip even though our prefix had already admitted this call.
            PlayerAdvanceObservationWindow.EndAdvance(admitted, -1, originalRan: false);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? skipped));
            AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(skipped).CloseReason);
            AssertEx.Equal(0L, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot().ActiveWindowSequence);
            long replay = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.EndAdvance(replay, 1);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? replayed));
            AssertEx.Equal(replay, AssertEx.NotNull(replayed).CallChainId);
            AssertEx.Equal(AdvanceWindowCloseReason.Completed, replayed!.CloseReason);
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(admitted);
            CloseAndDrainQueue();
        }
    }

    public static void NestedAbortRejectsEveryUndrainedSiblingInCallChain()
    {
        CloseAndDrainQueue();
        long outer = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("outer identity");
            long first = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.Capture("first nested identity");
            long second = PlayerAdvanceObservationWindow.BeginAdvance(2, 7);
            PlayerAdvanceObservationWindow.Capture("second nested partial identity");
            PlayerAdvanceObservationWindow.AbortAdvance(second);
            PlayerAdvanceObservationWindow.EndAdvance(first, 2);
            PlayerAdvanceObservationWindow.EndAdvance(outer, 2);
            for (int index = 0; index < 3; index++)
            {
                AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? pending));
                AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(pending).CloseReason);
                AssertEx.Equal(outer, pending!.CallChainId);
            }
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));
            long replay = PlayerAdvanceObservationWindow.BeginAdvance(2, 7);
            PlayerAdvanceObservationWindow.EndAdvance(replay, 2);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? replayed));
            AssertEx.Equal(AdvanceWindowCloseReason.Completed, AssertEx.NotNull(replayed).CloseReason);
            AssertEx.Equal(replay, replayed!.CallChainId);
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            CloseAndDrainQueue();
        }
    }

    public static void NestedAbortDoesNotRewriteAlreadyConsumedSibling()
    {
        CloseAndDrainQueue();
        long outer = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("outer identity");
            long nested = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? observed));
            ClosedAdvanceWindow consumed = AssertEx.NotNull(observed);
            AssertEx.Equal(AdvanceWindowCloseReason.ReplacedByOverlappingPrefix, consumed.CloseReason);
            PlayerAdvanceObservationWindow.AbortAdvance(nested);
            PlayerAdvanceObservationWindow.EndAdvance(outer, 1);
            AssertEx.Equal(AdvanceWindowCloseReason.ReplacedByOverlappingPrefix, consumed.CloseReason);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? pending));
            AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(pending).CloseReason);
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            CloseAndDrainQueue();
        }
    }

    public static void ManagedCallChainSurvivesSequentialNestedAdvances()
    {
        CloseAndDrainQueue();
        long outer = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("first empty row");
            long first = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.Capture("second empty row");
            PlayerAdvanceObservationWindow.EndAdvance(first, 2);

            // The outer call is still running even though the active capture
            // has closed. Another nested call belongs to that same chain.
            long second = PlayerAdvanceObservationWindow.BeginAdvance(2, 7);
            PlayerAdvanceObservationWindow.Capture("third row");
            PlayerAdvanceObservationWindow.EndAdvance(second, 3);
            PlayerAdvanceObservationWindow.EndAdvance(outer, 3);
            AssertEx.Equal(0L, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot().ActiveWindowSequence);
            for (int index = 0; index < 3; index++)
            {
                AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? captured));
                ClosedAdvanceWindow window = AssertEx.NotNull(captured);
                AssertEx.Equal(outer, window.CallChainId);
                AssertEx.Equal(7L, window.StorageRevision);
                AssertEx.Equal(index + 1, window.PlaybackRowIndex);
            }
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));
            long replay = PlayerAdvanceObservationWindow.BeginAdvance(3, 7);
            PlayerAdvanceObservationWindow.EndAdvance(replay, 3);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? replayed));
            AssertEx.Equal(replay, AssertEx.NotNull(replayed).CallChainId);
            AssertEx.True(replay > outer);
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            CloseAndDrainQueue();
        }
    }

    public static void BlockedAdvanceStateDoesNotIssueOrCloseWindows()
    {
        CloseAndDrainQueue();
        AdvanceWindowLifecycleSnapshot before = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
        // A voice-blocked prefix keeps Harmony state at zero. Its callbacks
        // must not issue a sequence or affect another admitted outer advance.
        PlayerAdvanceObservationWindow.EndAdvance(0, 4);
        PlayerAdvanceObservationWindow.AbortAdvance(0);
        AssertEx.Equal(before, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot());
        long admitted = PlayerAdvanceObservationWindow.BeginAdvance(4, 7);
        try
        {
            AdvanceWindowLifecycleSnapshot open = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
            PlayerAdvanceObservationWindow.EndAdvance(0, 5);
            PlayerAdvanceObservationWindow.AbortAdvance(0);
            AssertEx.Equal(open, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot());
            PlayerAdvanceObservationWindow.EndAdvance(admitted, 5);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? window));
            AssertEx.Equal(admitted, AssertEx.NotNull(window).Sequence);
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(admitted);
            CloseAndDrainQueue();
        }
    }

    public static void AbortedOrMisorderedCallCannotLeakIntoNextReplay()
    {
        CloseAndDrainQueue();
        long outer = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
        try
        {
            PlayerAdvanceObservationWindow.Capture("partial script");
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? aborted));
            AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(aborted).CloseReason);
            AssertEx.Equal(0L, PlayerAdvanceObservationWindow.ReadLifecycleSnapshot().ActiveWindowSequence);
            // A delayed postfix/finalizer for this finished call is harmless.
            PlayerAdvanceObservationWindow.EndAdvance(outer, 1);
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));

            long next = PlayerAdvanceObservationWindow.BeginAdvance(0, 7);
            long nested = PlayerAdvanceObservationWindow.BeginAdvance(1, 7);
            PlayerAdvanceObservationWindow.EndAdvance(next, 2);
            PlayerAdvanceObservationWindow.EndAdvance(nested, 2);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out _)); // superseded next
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? misordered));
            AssertEx.Equal(AdvanceWindowCloseReason.Aborted, AssertEx.NotNull(misordered).CloseReason);
            AssertEx.False(PlayerAdvanceObservationWindow.TryDequeue(out _));
            long replay = PlayerAdvanceObservationWindow.BeginAdvance(2, 7);
            PlayerAdvanceObservationWindow.EndAdvance(replay, 2);
            AssertEx.True(PlayerAdvanceObservationWindow.TryDequeue(out ClosedAdvanceWindow? replayed));
            AssertEx.Equal(replay, AssertEx.NotNull(replayed).CallChainId);
        }
        finally
        {
            PlayerAdvanceObservationWindow.AbortAdvance(outer);
            CloseAndDrainQueue();
        }
    }

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
