using Rukari.CharacterVoice.Core;

internal static class VoiceManualAdvanceTests
{
    private const string Voice = "aavt/voice/test";

    internal static void ManualInputBypassesEveryOwnedWaitEvenWhileAutoIsEnabled()
    {
        foreach (VoicePlaybackPhase phase in WaitingPhases)
        {
            VoicePlaybackGate gate = Waiting(phase);
            True(!gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: true), phase.ToString());
            True(!gate.BlocksAdvance(false, false, autoEnabled: false, manualInput: true), phase.ToString());
            Equal(phase, gate.Phase); // Admission alone does not cancel or manufacture a request.
        }
    }

    internal static void AutoAloneRetainsEveryWaitAndPreviewNeverBlocks()
    {
        foreach (VoicePlaybackPhase phase in WaitingPhases)
        {
            VoicePlaybackGate gate = Waiting(phase);
            True(gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: false), phase.ToString());
            True(gate.BlocksAdvance(false, false), "Existing two-argument callers retain AUTO behavior.");
            True(!gate.BlocksAdvance(true, false, autoEnabled: true, manualInput: false));
            True(!gate.BlocksAdvance(false, true, autoEnabled: true, manualInput: false));
        }
    }

    internal static void SwitchingToAutoUsesTheCurrentRequestWithoutRestartingItsDeadline()
    {
        VoicePlaybackGate gate = Waiting(VoicePlaybackPhase.Playing);
        long request = gate.RequestSequence;
        True(!gate.BlocksAdvance(false, false, autoEnabled: false, manualInput: false));
        True(gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: false));
        Equal(request, gate.RequestSequence);
        Equal(VoicePlaybackPhase.Playing, gate.Phase);
        True(gate.ObserveNativeWait(false, 5, 300));
        Equal(VoicePlaybackPhase.PostPlaybackDelay, gate.Phase);
        True(!gate.BlocksAdvance(false, false, autoEnabled: false, manualInput: false));
        True(gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: false));
        True(gate.ObserveNativeWait(false, 6.99, 300));
        True(!gate.ObserveNativeWait(false, 7, 300));
        True(!gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: false));
        Equal(request, gate.RequestSequence);
    }

    internal static void ManualCancellationInvalidatesLateLegacyReplayAndOldPlaybackObservations()
    {
        foreach (VoicePlaybackPhase phase in WaitingPhases)
        {
            VoicePlaybackGate gate = Waiting(phase);
            long canceledRequest = gate.RequestSequence;
            gate.Cancel("manual-input");
            Equal(VoicePlaybackPhase.Canceled, gate.Phase);
            True(!gate.Active && !gate.Pending);
            True(!gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: false));
            True(!gate.IsCurrent(canceledRequest, Voice), "Legacy loaded replay requires current active ownership.");
            True(!gate.MarkStarting(canceledRequest, 5));
            True(!gate.ObservePlayback(canceledRequest, true, 5, 300));
            True(!gate.Fail(canceledRequest, "late-load"));
            True(!gate.Expire(999));
            Equal(VoicePlaybackPhase.Canceled, gate.Phase);
        }
    }

    internal static void ANewRequestCannotBeChangedByTheCanceledRequestEvenWithTheSameVoiceKey()
    {
        VoicePlaybackGate gate = Waiting(VoicePlaybackPhase.Loading);
        long oldRequest = gate.RequestSequence;
        gate.Cancel("manual-input");
        long currentRequest = gate.Begin(Voice, 10, 10, 0);
        True(currentRequest > oldRequest);
        True(gate.IsCurrent(currentRequest, Voice));
        True(!gate.IsCurrent(oldRequest, Voice));
        True(!gate.MarkStarting(oldRequest, 11));
        True(!gate.ObservePlayback(oldRequest, true, 11, 300));
        True(!gate.Fail(oldRequest, "old-load-failed"));
        Equal(VoicePlaybackPhase.Loading, gate.Phase);
        True(gate.BlocksAdvance(false, false, autoEnabled: true, manualInput: false));
        True(gate.MarkStarting(currentRequest, 11));
        True(gate.ObservePlayback(currentRequest, true, 11, 300));
        True(!gate.ObservePlayback(oldRequest, false, 12, 300));
        Equal(VoicePlaybackPhase.Playing, gate.Phase);
        True(!gate.ObserveNativeWait(false, 13, 300));
        Equal(VoicePlaybackPhase.Completed, gate.Phase);
    }

    private static readonly VoicePlaybackPhase[] WaitingPhases =
    {
        VoicePlaybackPhase.Loading,
        VoicePlaybackPhase.Starting,
        VoicePlaybackPhase.Playing,
        VoicePlaybackPhase.PostPlaybackDelay
    };

    private static VoicePlaybackGate Waiting(VoicePlaybackPhase phase)
    {
        var gate = new VoicePlaybackGate();
        long request = gate.Begin(Voice, 0, 10, 2);
        if (phase == VoicePlaybackPhase.Loading) return gate;
        True(gate.MarkStarting(request, 1));
        if (phase == VoicePlaybackPhase.Starting) return gate;
        True(gate.ObservePlayback(request, true, 1, 300));
        if (phase == VoicePlaybackPhase.Playing) return gate;
        True(gate.ObserveNativeWait(false, 5, 300));
        Equal(VoicePlaybackPhase.PostPlaybackDelay, gate.Phase);
        return gate;
    }

    private static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
}
