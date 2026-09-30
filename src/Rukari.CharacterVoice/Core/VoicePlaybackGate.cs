namespace Rukari.CharacterVoice.Core;

public enum VoicePlaybackPhase
{
    Idle,
    Loading,
    Starting,
    Playing,
    Completed,
    Failed,
    Canceled,
    PostPlaybackDelay
}

/// <summary>Managed request ownership and wait policy; never retains an engine object.</summary>
public sealed class VoicePlaybackGate
{
    private double _deadline;
    private double _playbackDeadline;
    private bool _hasPlaybackDeadline;
    private double _postPlaybackDelay;

    public long RequestSequence { get; private set; }
    public string? VoiceIdentifier { get; private set; }
    public VoicePlaybackPhase Phase { get; private set; }
    public string? LastReason { get; private set; }
    public bool Active => Phase is VoicePlaybackPhase.Loading or VoicePlaybackPhase.Starting or VoicePlaybackPhase.Playing or VoicePlaybackPhase.PostPlaybackDelay;
    public bool HoldNativeVoiceWait => Phase is VoicePlaybackPhase.Loading or VoicePlaybackPhase.Starting or VoicePlaybackPhase.PostPlaybackDelay;
    public double PostPlaybackDelaySeconds => _postPlaybackDelay;
    /// <summary>True while the gate owns a bounded wait in any phase, so the tick must keep polling it.</summary>
    public bool Pending => Active;

    public long Begin(string identifier, double now, double loadingTimeout)
        => Begin(identifier, now, loadingTimeout, 0);

    public long Begin(string identifier, double now, double loadingTimeout, double postPlaybackDelaySeconds)
    {
        if (!VoiceDirectivePolicy.TryDecodeNativeIdentifier(identifier, out _))
            throw new ArgumentException("Only an owned voice identifier can create a playback gate.", nameof(identifier));
        if (!double.IsFinite(now) || !double.IsFinite(loadingTimeout) || loadingTimeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadingTimeout));

        RequestSequence++;
        VoiceIdentifier = identifier;
        Phase = VoicePlaybackPhase.Loading;
        _deadline = now + loadingTimeout;
        _playbackDeadline = 0;
        _hasPlaybackDeadline = false;
        // Snapshot once per request. Moving the global slider must not stretch an in-flight wait.
        _postPlaybackDelay = VoicePlaybackDelayPolicy.Normalize(postPlaybackDelaySeconds);
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

    public bool ObservePlayback(long sequence, bool nativePlaying, double now, double playbackTimeout)
    {
        if (sequence != RequestSequence) return false;
        if (Phase == VoicePlaybackPhase.PostPlaybackDelay) return CompleteDelay(now);
        if (Phase == VoicePlaybackPhase.Starting && nativePlaying)
        {
            Phase = VoicePlaybackPhase.Playing;
            // Arm the playback deadline on entry. The engine reports completion through the same predicate,
            // so a clip that never stops reporting as playing must still release the dialogue wait.
            _playbackDeadline = now + playbackTimeout;
            _hasPlaybackDeadline = true;
            return true;
        }
        if (Phase == VoicePlaybackPhase.Playing && !nativePlaying)
        {
            Phase = _postPlaybackDelay > 0 ? VoicePlaybackPhase.PostPlaybackDelay : VoicePlaybackPhase.Completed;
            _deadline = now + _postPlaybackDelay;
            _hasPlaybackDeadline = false;
            LastReason = "audio-finished";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Observe the raw native predicate BEFORE returning it to the game's wait coroutine. Otherwise the
    /// coroutine can exit on false before Update notices the end of the clip, and its one advance call is lost.
    /// </summary>
    public bool ObserveNativeWait(bool nativePlaying, double now, double playbackTimeout)
    {
        ObservePlayback(RequestSequence, nativePlaying, now, playbackTimeout);
        return nativePlaying || HoldNativeVoiceWait;
    }

    private bool CompleteDelay(double now)
    {
        if (Phase != VoicePlaybackPhase.PostPlaybackDelay || now < _deadline) return false;
        Phase = VoicePlaybackPhase.Completed;
        LastReason = "post-playback-delay-finished";
        return true;
    }

    /// <summary>
    /// Releases an expired wait. Loading and Starting hold the native predicate, so their timeout frees the
    /// dialogue; Playing also needs its own deadline. A normal post-playback delay completes here without
    /// reporting a failure: the return value is true only when a safety timeout failed the request.
    /// </summary>
    public bool Expire(double now)
    {
        if (Phase == VoicePlaybackPhase.PostPlaybackDelay)
        {
            CompleteDelay(now);
            return false;
        }
        if (Phase == VoicePlaybackPhase.Playing)
        {
            if (!_hasPlaybackDeadline || now < _playbackDeadline) return false;
            return Fail(RequestSequence, "playback-timeout");
        }
        if (!HoldNativeVoiceWait || now < _deadline) return false;
        return Fail(RequestSequence, Phase == VoicePlaybackPhase.Loading ? "loading-timeout" : "playback-did-not-start");
    }

    public bool Fail(long sequence, string reason)
    {
        if (!Active || sequence != RequestSequence) return false;
        Phase = VoicePlaybackPhase.Failed;
        _hasPlaybackDeadline = false;
        LastReason = reason;
        return true;
    }

    public void Cancel(string reason)
    {
        RequestSequence++;
        Phase = VoicePlaybackPhase.Canceled;
        VoiceIdentifier = null;
        _hasPlaybackDeadline = false;
        LastReason = reason;
    }

    public bool BlocksAdvance(bool previewRequested, bool playerIsPreview) =>
        Active && !previewRequested && !playerIsPreview;
}
