using Rukari.SpineSupport.Spines;

namespace Rukari.SpineSupport.Tests;

internal static class LobbyEntranceAdvancePolicyTests
{
    public static void OnlyTheCapturedEntranceEntryMayReportCompletion()
    {
        TrackEqual(LobbyEntranceTrackDecision.Playing, Observe(17, "Start_Idle_01", 0.9f, 1f));
        TrackEqual(LobbyEntranceTrackDecision.Completed, Observe(17, "Start_Idle_01", 1f, 1f));
        TrackEqual(LobbyEntranceTrackDecision.Completed, Observe(17, "Start_Idle_01", 1.1f, 1f));
    }

    public static void ClearedOrReplacedTracksNeverImpersonateACompletedEntrance()
    {
        TrackEqual(LobbyEntranceTrackDecision.Dropped, Observe(0, null, 10f, 1f));
        TrackEqual(LobbyEntranceTrackDecision.Dropped, Observe(18, "Start_Idle_01", 10f, 1f));
        TrackEqual(LobbyEntranceTrackDecision.Dropped, Observe(17, "<empty>", 10f, 1f));
        TrackEqual(LobbyEntranceTrackDecision.Dropped, Observe(17, "Idle_01", 10f, 1f));
        TrackEqual(LobbyEntranceTrackDecision.Dropped, Observe(17, null, 10f, 1f));
    }

    public static void InvalidTrackTimesDoNotAuthorizeOrKeepAnEntranceWatch()
    {
        foreach ((float time, float end) in new[]
                 {
                     (float.NaN, 1f), (float.PositiveInfinity, 1f), (-1f, 1f),
                     (1f, float.NaN), (1f, float.PositiveInfinity), (1f, 0f), (1f, -1f),
                 })
            TrackEqual(LobbyEntranceTrackDecision.Dropped, Observe(17, "Start_Idle_01", time, end));
    }

    private static LobbyEntranceTrackDecision Observe(int entry, string? animation, float time, float end) =>
        LobbyEntranceAdvancePolicy.ObserveTrack(new IntPtr(17), new IntPtr(entry), "Start_Idle_01", animation, time, end);

    private static void TrackEqual(LobbyEntranceTrackDecision expected, LobbyEntranceTrackDecision actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    public static void ALongVoiceNeverAcquiresAnEntranceTimeout()
    {
        // The old implementation armed a ten-second deadline irrespective of the native audio predicate.
        // A still-playing native clip must keep its official voice step in charge. The new policy deliberately
        // has no elapsed-time input: duration cannot convert this decision into permission to skip the clip.
        Equal(LobbyEntranceAdvanceDecision.LeaveToOfficialVoice,
            LobbyEntranceAdvancePolicy.Decide(true, true, true, true), "long native voice at entrance completion");
    }

    public static void AnOwnedVoiceTailDelayIsRespectedWithoutTheNativeVoiceFlag()
    {
        // CharacterVoice's getter remains true during its quiet tail delay. A native hasVoice flag alone
        // cannot be the guard, because the mod's own voice binding can carry the waiting responsibility.
        Equal(LobbyEntranceAdvanceDecision.LeaveToOfficialVoice,
            LobbyEntranceAdvancePolicy.Decide(true, true, false, true), "quiet post-playback delay");
    }

    public static void AVoiceThatEndedBeforeTheEntranceStillKeepsOfficialAutoWaiting()
    {
        // The first observation can already be false: the voice finished while the entrance was running.
        // This must not be classified as a missing clip or acquire a later forced-advance deadline.
        Equal(LobbyEntranceAdvanceDecision.LeaveToOfficialVoice,
            LobbyEntranceAdvancePolicy.Decide(true, true, true, false), "official AUTO wait after a completed clip");
    }

    public static void AReportedVoiceWithNoAudibleClipIsNotProofOfFailure()
    {
        // An absent/late clip, a preload still in flight, and an AUTO wait can produce the same snapshot.
        // With no safely associated failure signal the policy must not guess and skip the line.
        foreach (bool? sample in new bool?[] { false, null })
        {
            Equal(LobbyEntranceAdvanceDecision.LeaveToOfficialVoice,
                LobbyEntranceAdvancePolicy.Decide(true, true, true, sample), "unknown voice outcome");
        }
    }

    public static void AnUnavailableAudioManagerDoesNotTurnIntoAConfirmedSilentLine()
    {
        Equal(LobbyEntranceAdvanceDecision.VoiceStateUnavailable,
            LobbyEntranceAdvancePolicy.Decide(true, true, false, null), "audio manager unavailable during scene teardown");
    }

    public static void OnlyTheSameSilentLineCanAdvanceAfterItsEntrance()
    {
        Equal(LobbyEntranceAdvanceDecision.Advance,
            LobbyEntranceAdvancePolicy.Decide(true, true, false, false), "ordinary silent entrance");
        Equal(LobbyEntranceAdvanceDecision.StaleEntrance,
            LobbyEntranceAdvancePolicy.Decide(true, false, false, false), "player, cursor or preview mode changed");
    }

    public static void DisablingEntranceAdvanceAlsoKeepsSilentLinesWithTheOfficialPlayer()
    {
        Equal(LobbyEntranceAdvanceDecision.Disabled,
            LobbyEntranceAdvancePolicy.Decide(false, true, false, false), "author disabled entrance advance");
    }

    private static void Equal(LobbyEntranceAdvanceDecision expected, LobbyEntranceAdvanceDecision actual, string scenario)
    {
        if (expected != actual) throw new InvalidOperationException($"{scenario}: expected {expected}, got {actual}.");
    }
}
