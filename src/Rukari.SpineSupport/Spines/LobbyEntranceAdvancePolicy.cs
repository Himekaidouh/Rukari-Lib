namespace Rukari.SpineSupport.Spines;

/// <summary>
/// Finishing an entrance animation is not evidence that a voice failed to load. In particular, false from the
/// audio getter can also mean that a clip already ended and the official AUTO wait is still running.
/// </summary>
internal static class LobbyEntranceAdvancePolicy
{
    internal static LobbyEntranceTrackDecision ObserveTrack(IntPtr expectedEntry, IntPtr currentEntry,
        string expectedAnimation, string? currentAnimation, float trackTime, float animationEnd)
    {
        // A disappearing/replaced track is not completion evidence: the same player can show a new node at
        // the same cursor. Only the specific entry captured at ArmEntrance may authorize an extra advance.
        if (expectedEntry == IntPtr.Zero || currentEntry != expectedEntry
            || !string.Equals(expectedAnimation, currentAnimation, StringComparison.Ordinal)
            || !float.IsFinite(trackTime) || !float.IsFinite(animationEnd)
            || trackTime < 0f || animationEnd <= 0f)
            return LobbyEntranceTrackDecision.Dropped;
        return trackTime >= animationEnd ? LobbyEntranceTrackDecision.Completed : LobbyEntranceTrackDecision.Playing;
    }

    internal static LobbyEntranceAdvanceDecision Decide(
        bool enabled,
        bool samePlaybackLine,
        bool lineHasVoice,
        bool? voiceIsPlayingOrWaiting)
    {
        if (!samePlaybackLine) return LobbyEntranceAdvanceDecision.StaleEntrance;
        if (!enabled) return LobbyEntranceAdvanceDecision.Disabled;
        // This flag is the official line's responsibility, not a promise that sound is currently audible.
        // Once a line carries a voice, only its official voice/AUTO path may decide when it is done.
        if (lineHasVoice || voiceIsPlayingOrWaiting == true)
            return LobbyEntranceAdvanceDecision.LeaveToOfficialVoice;
        if (!voiceIsPlayingOrWaiting.HasValue)
            return LobbyEntranceAdvanceDecision.VoiceStateUnavailable;
        return LobbyEntranceAdvanceDecision.Advance;
    }
}

internal enum LobbyEntranceTrackDecision
{
    Playing,
    Completed,
    Dropped,
}

internal enum LobbyEntranceAdvanceDecision
{
    Advance,
    StaleEntrance,
    Disabled,
    LeaveToOfficialVoice,
    VoiceStateUnavailable,
}
