namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Primitive DataList/log association. RequestId is an opaque official value,
/// including zero; it is never interpreted as an editor scene index.
/// </summary>
public sealed record ManagedSelectionLogMarker(
    long Generation,
    int RequestId,
    long StartLogSequence,
    int MainThreadId,
    long WindowSequence,
    long ClosedWindowWatermark,
    EditorPreviewSceneAddress? CapturedScene);

public sealed record ManagedSelectionLogCandidate(
    ManagedSelectionLogMarker Marker,
    long LogSequence,
    CompiledScriptIdentity Script);

/// <summary>
/// Accepts only the first managed log in the same already-open advance window
/// as DataList, then requires a fresh, unchanged scene before publishing it.
/// This is identity correlation, not execution authorization. The caller must
/// serialize access; no native object or raw script text is retained here.
/// </summary>
public sealed class ManagedSelectionLogCorrelation
{
    public const int MaximumScriptCharacters = 512 * 1024;
    private long _generation;
    private ManagedSelectionLogMarker? _marker;
    private ManagedSelectionLogCandidate? _pending;
    private bool _firstLogObserved;

    public ManagedSelectionLogMarker Begin(
        int requestId,
        long startLogSequence,
        int mainThreadId,
        long windowSequence,
        long closedWindowWatermark,
        EditorPreviewSceneAddress? capturedScene)
    {
        _marker = new ManagedSelectionLogMarker(
            checked(++_generation), requestId, startLogSequence, mainThreadId,
            windowSequence, closedWindowWatermark, capturedScene);
        _pending = null;
        _firstLogObserved = false;
        return _marker;
    }

    public bool TryObserveFirst(
        long logSequence,
        CompiledScriptIdentity? script,
        int observedThreadId,
        long windowSequence,
        long closedWindowWatermark,
        out string error)
    {
        error = string.Empty;
        ManagedSelectionLogMarker? marker = _marker;
        if (marker == null)
        {
            error = "no-data-list-marker";
            return false;
        }

        if (_firstLogObserved)
        {
            error = "first-log-already-observed";
            return false;
        }

        // A rejected first message cannot be replaced with a later convenient
        // message. The verified rule is first-message, never latest/fuzzy.
        _firstLogObserved = true;
        if (marker.RequestId < 0 || marker.StartLogSequence < 0
            || marker.StartLogSequence == long.MaxValue
            || logSequence != marker.StartLogSequence + 1)
        {
            error = "log-sequence-not-first-after-marker";
            return false;
        }

        if (marker.MainThreadId <= 0 || observedThreadId != marker.MainThreadId)
        {
            error = "log-not-on-data-list-main-thread";
            return false;
        }

        if (marker.CapturedScene == null)
        {
            error = "data-list-scene-unavailable";
            return false;
        }

        if (marker.ClosedWindowWatermark < 0
            || marker.WindowSequence <= marker.ClosedWindowWatermark
            || windowSequence != marker.WindowSequence
            || closedWindowWatermark < marker.ClosedWindowWatermark
            || closedWindowWatermark >= windowSequence)
        {
            error = "log-outside-data-list-open-window";
            return false;
        }

        if (script == null || script.Utf16Length < 0
            || script.Utf16Length > MaximumScriptCharacters
            || script.LineCount <= 0 || script.LineCount > script.Utf16Length + 1
            || !CommandIdentity.TryNormalizeSha256(script.Sha256, out _))
        {
            error = "invalid-managed-script-identity";
            return false;
        }

        _pending = new ManagedSelectionLogCandidate(marker, logSequence, script);
        return true;
    }

    public ManagedSelectionLogCandidate? Pending => _pending;

    public bool IsPending(ManagedSelectionLogCandidate candidate) =>
        _pending == candidate && _marker == candidate.Marker;

    public bool TryComplete(
        ManagedSelectionLogCandidate candidate,
        int currentThreadId,
        long currentWindowSequence,
        EditorPreviewSceneAddress? liveScene,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        error = string.Empty;
        if (!IsPending(candidate))
        {
            error = "superseded-or-completed-data-list-marker";
            return false;
        }

        // Only this candidate is consumed. A stale queued operation cannot
        // consume a newer marker, even if its opaque RequestId was reused.
        _pending = null;
        if (currentThreadId != candidate.Marker.MainThreadId)
        {
            error = "confirmation-not-on-data-list-main-thread";
            return false;
        }

        // The verified preview cascade may close/replace several windows
        // before its managed pump runs. The log was already captured inside
        // the exact DataList window; marker/scene drift, not later siblings,
        // decides whether this selection observation is still current.
        if (currentWindowSequence < candidate.Marker.WindowSequence)
        {
            error = "confirmation-window-watermark-regressed";
            return false;
        }

        if (liveScene == null || liveScene != candidate.Marker.CapturedScene)
        {
            error = "live-scene-drift-from-data-list-capture";
            return false;
        }

        return true;
    }
}
