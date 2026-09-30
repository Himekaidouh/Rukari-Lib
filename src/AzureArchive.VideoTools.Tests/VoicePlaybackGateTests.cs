using AzureArchive.VideoTools.Core.Voices;

namespace AzureArchive.VideoTools.Tests;

internal static class VoicePlaybackGateTests
{
    private const string First = "aavt/voice/first";
    private const string Second = "aavt/voice/second";

    public static void LoadingHoldsNativeWaitAndOrdinaryAdvance()
    {
        var gate = new VoicePlaybackGate();
        gate.Begin(First, 0, 10);
        AssertEx.True(gate.HoldNativeVoiceWait);
        AssertEx.True(gate.BlocksAdvance(false, false));
        AssertEx.False(gate.BlocksAdvance(true, false));
        AssertEx.False(gate.BlocksAdvance(false, true));
    }

    public static void AudioMustStartBeforeFalseCanMeanFinished()
    {
        var gate = new VoicePlaybackGate();
        long token = gate.Begin(First, 0, 10);
        AssertEx.False(gate.ObservePlayback(token, false));
        gate.MarkStarting(token, 2);
        AssertEx.False(gate.ObservePlayback(token, false));
        AssertEx.Equal(VoicePlaybackPhase.Starting, gate.Phase);
        AssertEx.True(gate.HoldNativeVoiceWait);
        AssertEx.True(gate.ObservePlayback(token, true));
        AssertEx.Equal(VoicePlaybackPhase.Playing, gate.Phase);
        AssertEx.False(gate.HoldNativeVoiceWait);
        AssertEx.True(gate.BlocksAdvance(false, false));
    }

    public static void CompletedAudioReleasesBothWaits()
    {
        var gate = new VoicePlaybackGate();
        long token = gate.Begin(First, 0, 10);
        gate.MarkStarting(token, 1);
        gate.ObservePlayback(token, true);
        AssertEx.False(gate.Expire(100));
        AssertEx.True(gate.ObservePlayback(token, false));
        AssertEx.Equal(VoicePlaybackPhase.Completed, gate.Phase);
        AssertEx.False(gate.Active);
        AssertEx.False(gate.BlocksAdvance(false, false));
        AssertEx.False(gate.HoldNativeVoiceWait);
    }

    public static void LateLoadAndPlaybackCannotReviveReplacedRequest()
    {
        var gate = new VoicePlaybackGate();
        long oldToken = gate.Begin(First, 0, 10);
        long currentToken = gate.Begin(Second, 1, 10);
        AssertEx.False(gate.IsCurrent(oldToken, First));
        AssertEx.False(gate.MarkStarting(oldToken, 2));
        AssertEx.False(gate.ObservePlayback(oldToken, true));
        AssertEx.False(gate.Fail(oldToken, "old-failure"));
        AssertEx.True(gate.IsCurrent(currentToken, Second));
        AssertEx.Equal(VoicePlaybackPhase.Loading, gate.Phase);
    }

    public static void CancelInvalidatesPendingLoadEvenWhenSameClipIsReused()
    {
        var gate = new VoicePlaybackGate();
        long oldToken = gate.Begin(First, 0, 10);
        gate.Cancel("preview-changed");
        AssertEx.False(gate.Active);
        AssertEx.False(gate.IsCurrent(oldToken, First));
        long newToken = gate.Begin(First, 2, 10);
        AssertEx.False(gate.MarkStarting(oldToken, 3));
        AssertEx.True(gate.IsCurrent(newToken, First));
    }

    public static void LoadingFailureCannotLeaveDialoguePermanentlyBlocked()
    {
        var gate = new VoicePlaybackGate();
        long token = gate.Begin(First, 0, 10);
        AssertEx.False(gate.Expire(9.99));
        AssertEx.True(gate.Expire(10));
        AssertEx.Equal(VoicePlaybackPhase.Failed, gate.Phase);
        AssertEx.Equal("loading-timeout", gate.LastReason);
        AssertEx.False(gate.BlocksAdvance(false, false));
        AssertEx.False(gate.MarkStarting(token, 11));
        AssertEx.False(gate.ObservePlayback(token, true));
    }

    public static void LoadedAudioThatNeverStartsAlsoReleasesWait()
    {
        var gate = new VoicePlaybackGate();
        long token = gate.Begin(First, 0, 10);
        gate.MarkStarting(token, 3);
        AssertEx.False(gate.Expire(3.99));
        AssertEx.True(gate.Expire(4));
        AssertEx.Equal("playback-did-not-start", gate.LastReason);
        AssertEx.False(gate.Active);
    }

    public static void NativeVoiceIdentifiersCannotAcquireTheModGate()
    {
        var gate = new VoicePlaybackGate();
        bool rejected = false;
        try { gate.Begin("Arona_Talk_01", 0, 10); }
        catch (ArgumentException) { rejected = true; }
        AssertEx.True(rejected);
        AssertEx.False(gate.Active);
        AssertEx.Equal(0L, gate.RequestSequence);
    }
}
