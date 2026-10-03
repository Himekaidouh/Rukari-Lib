using AzureArchive.VideoTools.Core.Playback;

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
    EditorPreviewSceneAddress? CapturedScene)
{
    // Kept out of the positional constructor to preserve the existing ABI.
    // Zero is the original strict-only contract.
    public long SelectionGeneration { get; init; }
}

public sealed record ManagedSelectionLogCandidate(
    ManagedSelectionLogMarker Marker,
    long LogSequence,
    CompiledScriptIdentity Script)
{
    public long ObservedWindowSequence { get; init; }
    public long ObservedClosedWindowWatermark { get; init; }
    public string TimingMismatchReason { get; init; } = string.Empty;
}

/// <summary>
/// Evidence supplied only after a healthy, unique continuous-compile candidate
/// has actually matched an eligible preview window and the live scene was read
/// again. It cannot create a selection identity without the real first log.
/// </summary>
public sealed record ManagedSelectionPreviewWindowProof(
    long SelectionGeneration,
    int RequestId,
    long MinimumWindowSequenceExclusive,
    long WindowSequence,
    CompiledScriptIdentity CompiledScript,
    int ExactCompiledScriptMessageCount,
    PlayerRuntimeContextSnapshot? RuntimeContext,
    EditorPreviewSceneAddress? LiveScene)
{
    public long IssuedWindowSequence { get; init; }
    public long ClosedWindowWatermark { get; init; }
}

/// <summary>
/// The strict path accepts only the first managed log in the same already-open
/// advance window as DataList, then requires a fresh, unchanged scene. Timing
/// rejection may retain that real first log separately until an explicit
/// verified preview-window proof confirms it; retention never publishes it.
/// This is identity correlation, not execution authorization. The caller must
/// serialize access; no native object or raw script text is retained here.
/// </summary>
public sealed class ManagedSelectionLogCorrelation
{
    public const int MaximumScriptCharacters = 512 * 1024;
    private long _generation;
    private ManagedSelectionLogMarker? _marker;
    private ManagedSelectionLogCandidate? _pending;
    private ManagedSelectionLogCandidate? _deferredPending;
    private bool _firstLogObserved;
    private string _firstLogWindowMismatchReason = string.Empty;

    public ManagedSelectionLogMarker Begin(
        int requestId,
        long startLogSequence,
        int mainThreadId,
        long windowSequence,
        long closedWindowWatermark,
        EditorPreviewSceneAddress? capturedScene)
        => Begin(requestId, startLogSequence, mainThreadId, windowSequence,
            closedWindowWatermark, capturedScene, 0);

    public ManagedSelectionLogMarker Begin(
        int requestId,
        long startLogSequence,
        int mainThreadId,
        long windowSequence,
        long closedWindowWatermark,
        EditorPreviewSceneAddress? capturedScene,
        long selectionGeneration)
    {
        _marker = new ManagedSelectionLogMarker(
            checked(++_generation), requestId, startLogSequence, mainThreadId,
            windowSequence, closedWindowWatermark, capturedScene)
        {
            SelectionGeneration = selectionGeneration
        };
        _pending = null;
        _deferredPending = null;
        _firstLogObserved = false;
        _firstLogWindowMismatchReason = string.Empty;
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
            _firstLogWindowMismatchReason = DescribeTimingMismatch(
                marker, windowSequence, closedWindowWatermark);
            error = "log-outside-data-list-open-window";
            // Timing rejection still has no strict publication authority.
            // Keep only the exact real first message when every non-timing
            // requirement and the complete capture are valid, and that real
            // message was observed while its own window was still open.
            // Closed containers retain strings, not log event sequences: a
            // first message after close cannot prove it was in that container.
            // A later message can never replace it.
            if (marker.SelectionGeneration > 0
                && IsValidScript(script)
                && IsFullScene(marker.CapturedScene)
                && marker.ClosedWindowWatermark >= 0
                && marker.WindowSequence >= marker.ClosedWindowWatermark
                && windowSequence >= marker.WindowSequence
                && closedWindowWatermark >= marker.ClosedWindowWatermark
                && closedWindowWatermark < windowSequence)
            {
                _deferredPending = new ManagedSelectionLogCandidate(marker, logSequence, script!)
                {
                    ObservedWindowSequence = windowSequence,
                    ObservedClosedWindowWatermark = closedWindowWatermark,
                    TimingMismatchReason = _firstLogWindowMismatchReason
                };
            }
            return false;
        }

        if (!IsValidScript(script))
        {
            error = "invalid-managed-script-identity";
            return false;
        }

        _deferredPending = null;
        _pending = new ManagedSelectionLogCandidate(marker, logSequence, script!)
        {
            ObservedWindowSequence = windowSequence,
            ObservedClosedWindowWatermark = closedWindowWatermark
        };
        return true;
    }

    public ManagedSelectionLogCandidate? Pending => _pending;
    public ManagedSelectionLogCandidate? DeferredPending => _deferredPending;
    public string FirstLogWindowMismatchReason => _firstLogWindowMismatchReason;

    public bool IsPending(ManagedSelectionLogCandidate candidate) =>
        _pending == candidate && _marker == candidate.Marker;

    public bool IsDeferredPending(ManagedSelectionLogCandidate candidate) =>
        _deferredPending == candidate && _marker == candidate.Marker;

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
        _deferredPending = null;
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

    public bool TryCompleteDeferred(
        ManagedSelectionLogCandidate candidate,
        int currentThreadId,
        ManagedSelectionPreviewWindowProof proof,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(proof);
        error = string.Empty;
        if (!IsDeferredPending(candidate))
        {
            error = "superseded-or-completed-data-list-marker";
            return false;
        }

        // As with strict completion, only the current candidate is consumed.
        // An old queued confirmation cannot consume a superseding DataList.
        _deferredPending = null;
        ManagedSelectionLogMarker marker = candidate.Marker;
        if (marker.MainThreadId <= 0 || currentThreadId != marker.MainThreadId)
        {
            error = "confirmation-not-on-data-list-main-thread";
            return false;
        }

        if (marker.SelectionGeneration <= 0
            || proof.SelectionGeneration != marker.SelectionGeneration
            || proof.RequestId < 0 || proof.RequestId != marker.RequestId
            || proof.MinimumWindowSequenceExclusive < 0
            || proof.MinimumWindowSequenceExclusive != marker.ClosedWindowWatermark)
        {
            error = "preview-proof-does-not-match-data-list-lease";
            return false;
        }

        if (!IsValidScript(proof.CompiledScript) || proof.CompiledScript != candidate.Script)
        {
            error = "preview-proof-compiled-script-drift-from-first-log";
            return false;
        }

        if (proof.ExactCompiledScriptMessageCount != 1)
        {
            error = "preview-proof-exact-script-message-count-not-one";
            return false;
        }

        PlayerRuntimeContextSnapshot? context = proof.RuntimeContext;
        if (context == null || !context.PlayerAvailable || !context.PreviewMode
            || context.Mode != PlayerRuntimeMode.EditorPreview)
        {
            error = "preview-proof-not-in-editor-preview-runtime";
            return false;
        }

        if (!IsFullScene(marker.CapturedScene) || !IsFullScene(proof.LiveScene)
            || proof.LiveScene != marker.CapturedScene)
        {
            error = "live-scene-drift-from-data-list-capture";
            return false;
        }

        if (proof.IssuedWindowSequence < candidate.ObservedWindowSequence
            || proof.IssuedWindowSequence < marker.WindowSequence
            || proof.ClosedWindowWatermark < candidate.ObservedClosedWindowWatermark
            || proof.ClosedWindowWatermark < marker.ClosedWindowWatermark
            || proof.ClosedWindowWatermark > proof.IssuedWindowSequence)
        {
            error = "preview-proof-window-watermark-regressed-or-invalid";
            return false;
        }

        if (proof.WindowSequence <= proof.MinimumWindowSequenceExclusive
            || proof.WindowSequence > proof.IssuedWindowSequence
            || proof.WindowSequence > proof.ClosedWindowWatermark)
        {
            error = "preview-proof-window-not-eligible";
            return false;
        }

        if (proof.WindowSequence != candidate.ObservedWindowSequence)
        {
            error = "preview-proof-window-not-first-log-window";
            return false;
        }

        return true;
    }

    private static bool IsValidScript(CompiledScriptIdentity? script) =>
        script != null && script.Utf16Length >= 0
        && script.Utf16Length <= MaximumScriptCharacters
        && script.LineCount > 0 && script.LineCount <= script.Utf16Length + 1
        && CommandIdentity.TryNormalizeSha256(script.Sha256, out _);

    private static bool IsFullScene(EditorPreviewSceneAddress? scene) =>
        scene != null && IsSceneFingerprint(scene.ProjectKey)
        && Guid.TryParseExact(scene.NodeGuid, "D", out _)
        && scene.SceneIndex >= 0 && IsSceneFingerprint(scene.Fingerprint);

    private static bool IsSceneFingerprint(string? value) =>
        value != null && value.Length == 24 && value.All(Uri.IsHexDigit);

    private static string DescribeTimingMismatch(
        ManagedSelectionLogMarker marker,
        long observedWindowSequence,
        long observedClosedWindowWatermark)
    {
        if (marker.ClosedWindowWatermark < 0)
            return "invalid-data-list-closed-watermark";
        if (marker.WindowSequence < marker.ClosedWindowWatermark)
            return "invalid-data-list-issued-watermark";
        if (observedWindowSequence < marker.WindowSequence)
            return "observed-issued-watermark-regressed";
        if (observedClosedWindowWatermark < marker.ClosedWindowWatermark)
            return "observed-closed-watermark-regressed";
        if (observedClosedWindowWatermark > observedWindowSequence)
            return "observed-closed-watermark-exceeds-issued";
        if (marker.WindowSequence == marker.ClosedWindowWatermark)
            return "no-open-window-at-data-list";
        if (observedWindowSequence != marker.WindowSequence)
            return "different-window-issued-after-data-list";
        return "data-list-window-already-closed";
    }
}
