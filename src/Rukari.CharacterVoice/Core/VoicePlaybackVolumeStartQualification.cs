namespace Rukari.CharacterVoice.Core;

// Existing raw playing observations only: never reads a clip or retains a callback.
// A callback-capable request must first observe quiet, then its first playing value.
// An initial true consumes this request's volume decision without granting it.
internal sealed class VoicePlaybackVolumeStartQualification
{
    internal long RequestSequence { get; private set; }
    internal bool SawQuiet { get; private set; }
    internal bool Decided { get; private set; }

    internal bool Begin(long requestSequence)
    {
        Reset();
        if (requestSequence <= 0) return false;
        RequestSequence = requestSequence;
        return true;
    }

    internal bool Observe(long requestSequence, bool rawNativePlaying)
    {
        if (RequestSequence == 0 || requestSequence != RequestSequence || Decided) return false;
        if (!rawNativePlaying) { SawQuiet = true; return false; }
        Decided = true;
        return SawQuiet;
    }

    internal void Reset()
    {
        RequestSequence = 0;
        SawQuiet = Decided = false;
    }
}
