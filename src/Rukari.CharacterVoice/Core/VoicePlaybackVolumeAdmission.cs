namespace Rukari.CharacterVoice.Core;

// Only primitive observations survive a callback. A source's first non-neutral request
// observes its baseline; a distinct later request on that source may attempt one write.
internal sealed class VoicePlaybackVolumeAdmission
{
    internal bool IsDisabled { get; private set; }
    internal int SourceIdentity { get; private set; }
    internal long ObservedRequest { get; private set; }

    internal bool NeedsObservation(long requestSequence, int sourceIdentity) => !IsDisabled
        && requestSequence > 0 && sourceIdentity != 0
        && (sourceIdentity != SourceIdentity || requestSequence != ObservedRequest);

    internal bool ObserveBaseline(long requestSequence, int sourceIdentity, float observedVolume)
    {
        if (IsDisabled) return false;
        if (requestSequence <= 0 || sourceIdentity == 0 || !float.IsFinite(observedVolume)
            || observedVolume < 0 || observedVolume > 1)
        {
            Disable();
            return false;
        }
        if (!NeedsObservation(requestSequence, sourceIdentity)) return false;
        bool mayWrite = SourceIdentity == sourceIdentity && ObservedRequest != 0;
        SourceIdentity = sourceIdentity;
        ObservedRequest = requestSequence;
        return mayWrite;
    }

    // Native observation/application failure disables only this subfeature for the session.
    internal void Disable() => IsDisabled = true;
}
