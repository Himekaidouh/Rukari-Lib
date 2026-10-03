namespace Rukari.CharacterVoice.Core;

// Pure managed ownership of one intended source-volume write. The runtime must freshly
// reacquire its owned source and perform reads/writes on the main thread. This object retains
// no Unity wrapper; TryRestore is a compare-before-restore policy, not an engine atomic CAS.
internal sealed class VoicePlaybackVolumeLease
{
    internal bool HasLease { get; private set; }
    internal bool HasApplied { get; private set; }
    internal bool WriteAttempted { get; private set; }
    internal long RequestSequence { get; private set; }
    internal int SourceIdentity { get; private set; }
    internal float BaselineVolume { get; private set; }
    internal float AppliedVolume { get; private set; }

    internal bool TryPrepare(long requestSequence, int sourceIdentity, float currentVolume,
        double percent, out float desiredVolume)
    {
        desiredVolume = 0;
        if (HasLease || requestSequence <= 0 || sourceIdentity == 0 || !float.IsFinite(currentVolume)
            || currentVolume < 0 || currentVolume > 1 || VoicePlaybackVolumePolicy.IsNeutral(percent))
            return false;
        desiredVolume = VoicePlaybackVolumePolicy.ScaleOfficialVolume(currentVolume, percent);
        if (desiredVolume == currentVolume) return false;
        HasLease = true;
        HasApplied = false;
        WriteAttempted = false;
        RequestSequence = requestSequence;
        SourceIdentity = sourceIdentity;
        BaselineVolume = currentVolume;
        AppliedVolume = desiredVolume;
        return true;
    }

    internal bool MarkWriteAttempted(long requestSequence, int sourceIdentity)
    {
        if (!HasLease || WriteAttempted || requestSequence != RequestSequence || sourceIdentity != SourceIdentity)
            return false;
        WriteAttempted = true;
        return true;
    }

    internal bool MarkApplied(long requestSequence, int sourceIdentity, float writtenVolume)
    {
        if (!HasLease || !WriteAttempted || HasApplied || requestSequence != RequestSequence || sourceIdentity != SourceIdentity
            || !float.IsFinite(writtenVolume) || writtenVolume != AppliedVolume)
            return false;
        HasApplied = true;
        return true;
    }

    internal bool TryRestore(long requestSequence, int sourceIdentity, float freshlyReadVolume,
        out float restoreVolume)
    {
        restoreVolume = 0;
        // A delayed cleanup from an old request must never release the replacement's lease.
        if (!HasLease || requestSequence != RequestSequence) return false;
        // Even a setter that throws, or a setter followed by failed readback, may have
        // changed native state. Keep its intended value until one independent cleanup:
        // only a fresh exact match permits restoring; preparation alone never permits it.
        bool restore = WriteAttempted && sourceIdentity == SourceIdentity && float.IsFinite(freshlyReadVolume)
            && freshlyReadVolume == AppliedVolume;
        if (restore) restoreVolume = BaselineVolume;
        // A same-request mismatch belongs to someone else's current source/settings. Drop our
        // stale claim rather than trying again later and overwriting that external change.
        HasLease = HasApplied = WriteAttempted = false;
        RequestSequence = 0;
        SourceIdentity = 0;
        BaselineVolume = AppliedVolume = 0;
        return restore;
    }
}
