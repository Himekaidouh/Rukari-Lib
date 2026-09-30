namespace AzureArchive.VideoTools.Core.Voices;

public enum VoicePlaybackPhase
{
    Idle,
    Loading,
    Starting,
    Playing,
    Completed,
    Failed,
    Canceled
}

/// <summary>Managed request ownership and wait policy; never retains an engine object.</summary>
public sealed class VoicePlaybackGate
{
    private double _deadline;

    public long RequestSequence { get; private set; }
    public string? VoiceIdentifier { get; private set; }
    public VoicePlaybackPhase Phase { get; private set; }
    public string? LastReason { get; private set; }
    public bool Active => Phase is VoicePlaybackPhase.Loading or VoicePlaybackPhase.Starting or VoicePlaybackPhase.Playing;
    public bool HoldNativeVoiceWait => Phase is VoicePlaybackPhase.Loading or VoicePlaybackPhase.Starting;

    public long Begin(string identifier, double now, double loadingTimeout)
    {
        if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(identifier, out _))
            throw new ArgumentException("Only an owned voice identifier can create a playback gate.", nameof(identifier));
        if (!double.IsFinite(now) || !double.IsFinite(loadingTimeout) || loadingTimeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadingTimeout));

        RequestSequence++;
        VoiceIdentifier = identifier;
        Phase = VoicePlaybackPhase.Loading;
        _deadline = now + loadingTimeout;
        LastReason = null;
        return RequestSequence;
    }

    public bool IsCurrent(long sequence, string identifier) =>
        Active && RequestSequence == sequence && string.Equals(VoiceIdentifier, identifier, StringComparison.Ordinal);

    public bool MarkStarting(long sequence, double now, double startTimeout = 1)
    {
        if (sequence != RequestSequence || Phase != VoicePlaybackPhase.Loading) return false;
        Phase = VoicePlaybackPhase.Starting;
        _deadline = now + startTimeout;
        return true;
    }

    public bool ObservePlayback(long sequence, bool nativePlaying)
    {
        if (sequence != RequestSequence) return false;
        if (Phase == VoicePlaybackPhase.Starting && nativePlaying)
        {
            Phase = VoicePlaybackPhase.Playing;
            return true;
        }
        if (Phase == VoicePlaybackPhase.Playing && !nativePlaying)
        {
            Phase = VoicePlaybackPhase.Completed;
            LastReason = "audio-finished";
            return true;
        }
        return false;
    }

    public bool Expire(double now)
    {
        if (!HoldNativeVoiceWait || now < _deadline) return false;
        return Fail(RequestSequence, Phase == VoicePlaybackPhase.Loading ? "loading-timeout" : "playback-did-not-start");
    }

    public bool Fail(long sequence, string reason)
    {
        if (!Active || sequence != RequestSequence) return false;
        Phase = VoicePlaybackPhase.Failed;
        LastReason = reason;
        return true;
    }

    public void Cancel(string reason)
    {
        RequestSequence++;
        Phase = VoicePlaybackPhase.Canceled;
        VoiceIdentifier = null;
        LastReason = reason;
    }

    public bool BlocksAdvance(bool previewRequested, bool playerIsPreview) =>
        Active && !previewRequested && !playerIsPreview;
}
