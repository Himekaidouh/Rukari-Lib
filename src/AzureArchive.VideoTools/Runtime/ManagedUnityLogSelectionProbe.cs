using System;
using System.Linq;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using BepInEx.Logging;
using Rukari.Lib;

namespace AzureArchive.VideoTools.Runtime;

internal static class ManagedUnityLogSelectionProbe
{
    private static readonly object Gate = new();
    private static readonly ManagedSelectionLogCorrelation Correlation = new();
    private static ManagedUnityLogListener? _listener;
    private static long _sequence;
    private static ManagedSelectionLogMarker? _marker;
    private static EditorInputSceneCaptureProof? _inputProof;

    public static void Install()
    {
        if (_listener != null) return;
        _listener = new ManagedUnityLogListener();
        BepInEx.Logging.Logger.Listeners.Add(_listener);
        Plugin.Logger.LogInfo(
            "Managed Unity-log selection probe installed; managed hashes are confirmed "
            + "by the existing main-thread pump, without a Selectable callback patch.");
    }

    public static void MarkDataList(int requestId, ApiResult<SceneSnapshot> capturedScene,
        EditorInputSceneCaptureProof? inputProof, long selectionGeneration)
    {
        ArgumentNullException.ThrowIfNull(capturedScene);
        IModRuntime? runtime = ModServices.Current;
        int mainThreadId = runtime is { State: RuntimeState.Ready, IsMainThread: true }
            ? Environment.CurrentManagedThreadId : 0;
        EditorPreviewSceneAddress? scene = capturedScene.Success && capturedScene.Value != null
            ? CoreScene(capturedScene.Value.Address) : null;
        lock (Gate)
        {
            _inputProof = inputProof;
            _marker = Correlation.Begin(requestId, _sequence, mainThreadId,
                PlayerAdvanceObservationWindow.IssuedSequence,
                PlayerAdvanceObservationWindow.ClosedSequenceWatermark, scene, selectionGeneration);
        }
    }

    // The listener only captures managed metadata. Existing Update/LateUpdate
    // calls this before draining preview windows; no native hook is added.
    public static void PumpOnMainThread()
    {
        IModRuntime? runtime = ModServices.Current;
        if (runtime is not { State: RuntimeState.Ready, IsMainThread: true }) return;
        ManagedSelectionLogCandidate? candidate;
        EditorInputSceneCaptureProof? inputProof;
        lock (Gate) { candidate = Correlation.Pending; inputProof = _inputProof; }
        if (candidate == null) return;
        lock (Gate)
        {
            if (!Correlation.IsPending(candidate)) return;
        }

        // This is the same fresh selected-scene API previously called by the
        // selection postfix. No new native property or retained wrapper is used.
        ApiResult<SceneSnapshot> liveScene = Plugin.Api.Editor.GetSelectedScene();
        EditorPreviewSceneAddress? live = liveScene.Success && liveScene.Value != null
            ? CoreScene(liveScene.Value.Address) : null;
        string inputError = string.Empty;
        if (inputProof != null)
        {
            var documents = runtime.GetService<Rukari.Lib.Editor.IEditorDocumentService>();
            var read = documents is { Success: true, Value: not null } ? documents.Value.ReadSelection() : null;
            var confirmation = EditorInputSceneCapturePlanner.Confirm(inputProof, read?.Success == true ? read.Value : null);
            if (!confirmation.Success) { live = null; inputError = confirmation.Error; }
        }
        string error;
        lock (Gate)
        {
            bool currentCandidate = Correlation.IsPending(candidate);
            bool completed = Correlation.TryComplete(candidate, Environment.CurrentManagedThreadId,
                PlayerAdvanceObservationWindow.IssuedSequence, live, out error);
            if (currentCandidate) _inputProof = null;
            if (completed)
            {
                EditorSelectionEventProbe.PublishManagedLog(candidate, liveScene);
                return;
            }
        }

        Plugin.Logger.LogWarning(
            $"Managed-log selection rejected: generation={candidate.Marker.Generation}; "
            + $"request={candidate.Marker.RequestId}; reason={error}; inputConfirmation={inputError}.");
    }

    // Called only after the current window claimed a healthy, unique lease and
    // reverified the generation's captured scene. The lease corroborates the
    // retained first message; it is never used to manufacture a log candidate.
    internal static bool ConfirmDeferredFromPreview(
        EmbeddedEditorPreviewLease lease,
        ClosedAdvanceWindow window,
        PlayerRuntimeContextSnapshot context,
        out EditorSceneIdentitySnapshot? confirmedIdentity,
        out ApiResult<SceneSnapshot>? confirmedScene,
        out string error)
    {
        confirmedIdentity = null;
        confirmedScene = null;
        error = string.Empty;
        IModRuntime? runtime = ModServices.Current;
        if (runtime is not { State: RuntimeState.Ready, IsMainThread: true })
        {
            error = "deferred-confirmation-runtime-or-main-thread-unavailable";
            return false;
        }

        ManagedSelectionLogCandidate? candidate;
        EditorInputSceneCaptureProof? inputProof;
        lock (Gate)
        {
            candidate = Correlation.DeferredPending;
            inputProof = _inputProof;
            if (candidate == null || !Correlation.IsDeferredPending(candidate))
            {
                error = "no-deferred-first-managed-log";
                return false;
            }
        }

        if (lease.ClaimedWindowSequence != window.Sequence
            || !EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(out var scope)
            || scope.GenerationId != lease.DataListGeneration
            || scope.MinimumWindowSequenceExclusive != lease.MinimumWindowSequenceExclusive)
        {
            error = "deferred-preview-generation-or-claimed-window-mismatch";
            return false;
        }

        // A later replay carrying the same hash cannot stand in for the first
        // actual log. Only its own observed window can supply corroboration.
        if (window.Sequence != candidate.ObservedWindowSequence)
        {
            error = "deferred-first-log-window-mismatch";
            return false;
        }

        AdvanceWindowAdmission admission = AdvanceWindowAdmissionPolicy.Classify(
            window.CloseReason switch
            {
                AdvanceWindowCloseReason.Completed => AdvanceWindowCloseKind.Completed,
                AdvanceWindowCloseReason.ReplacedByOverlappingPrefix =>
                    AdvanceWindowCloseKind.SupersededByOverlappingPrefix,
                _ => AdvanceWindowCloseKind.PrefixWithoutOpen
            },
            window.ManagedUnityMessages.Count,
            window.CaptureOverflowed,
            window.EmbeddedCommandConflict);
        if (admission == AdvanceWindowAdmission.Reject)
        {
            error = "deferred-preview-window-ineligible";
            return false;
        }

        int exactMessages = window.ManagedUnityMessages.Count(message =>
            CommandIdentity.CompiledScript(message) == lease.CompiledScript);
        // Reuse the existing selected-scene and shared-document reads. The
        // DataList input proof remains retained until this candidate completes
        // or a newer DataList supersedes it.
        ApiResult<SceneSnapshot> liveScene = Plugin.Api.Editor.GetSelectedScene();
        EditorPreviewSceneAddress? live = liveScene.Success && liveScene.Value != null
            ? CoreScene(liveScene.Value.Address) : null;
        string inputError = string.Empty;
        if (inputProof != null)
        {
            var documents = runtime.GetService<Rukari.Lib.Editor.IEditorDocumentService>();
            var read = documents is { Success: true, Value: not null } ? documents.Value.ReadSelection() : null;
            var confirmation = EditorInputSceneCapturePlanner.Confirm(inputProof, read?.Success == true ? read.Value : null);
            if (!confirmation.Success) { live = null; inputError = confirmation.Error; }
        }

        var proof = new ManagedSelectionPreviewWindowProof(
            lease.DataListGeneration,
            lease.DataListRequestId,
            lease.MinimumWindowSequenceExclusive,
            window.Sequence,
            lease.CompiledScript,
            exactMessages,
            context,
            live)
        {
            IssuedWindowSequence = PlayerAdvanceObservationWindow.IssuedSequence,
            ClosedWindowWatermark = PlayerAdvanceObservationWindow.ClosedSequenceWatermark
        };
        lock (Gate)
        {
            bool currentCandidate = Correlation.IsDeferredPending(candidate);
            bool completed = Correlation.TryCompleteDeferred(
                candidate, Environment.CurrentManagedThreadId, proof, out error);
            if (currentCandidate) _inputProof = null;
            if (completed)
            {
                confirmedIdentity = EditorSelectionEventProbe.PublishManagedLog(candidate, liveScene,
                    "managed-log-first-message-data-list-preview-window-confirmation", window.Sequence);
                confirmedScene = liveScene;
            }
        }

        Plugin.Logger.LogInfo(
            $"Managed-log deferred selection {(confirmedIdentity == null ? "rejected" : "confirmed")}: "
            + $"generation={candidate.Marker.Generation}; selectionGeneration={candidate.Marker.SelectionGeneration}; "
            + $"request={candidate.Marker.RequestId}; unitySeq={candidate.LogSequence}; "
            + $"markerIssued={candidate.Marker.WindowSequence}; markerClosed={candidate.Marker.ClosedWindowWatermark}; "
            + $"observedIssued={candidate.ObservedWindowSequence}; observedClosed={candidate.ObservedClosedWindowWatermark}; "
            + $"proofWindow={window.Sequence}; proofIssued={proof.IssuedWindowSequence}; proofClosed={proof.ClosedWindowWatermark}; "
            + $"exactMessages={exactMessages}; reason={error}; inputConfirmation={inputError}; "
            + "proofSource=current-healthy-unique-live-verified-editor-preview-window; executionAuthorized=false.");
        return confirmedIdentity != null;
    }

    private static void Capture(LogEventArgs eventArgs)
    {
        if (!string.Equals(eventArgs.Source.SourceName, "Unity", StringComparison.Ordinal)
            || eventArgs.Data is not string text) return;
        string error;
        ManagedSelectionLogMarker? marker;
        long observedIssued;
        long observedClosed;
        bool observed;
        bool deferred;
        lock (Gate)
        {
            long sequence = ++_sequence;
            marker = _marker;
            observedIssued = PlayerAdvanceObservationWindow.IssuedSequence;
            observedClosed = PlayerAdvanceObservationWindow.ClosedSequenceWatermark;
            CompiledScriptIdentity? identity = text.Length <= ManagedSelectionLogCorrelation.MaximumScriptCharacters
                ? CommandIdentity.CompiledScript(text) : null;
            observed = Correlation.TryObserveFirst(sequence, identity, Environment.CurrentManagedThreadId,
                observedIssued, observedClosed, out error);
            deferred = Correlation.DeferredPending != null;
        }

        if (error is "no-data-list-marker" or "first-log-already-observed") return;
        string diagnostic =
            $"Managed-log first selection message state={(observed ? "pending" : deferred ? "deferred" : "rejected")}; "
            + $"generation={marker?.Generation}; selectionGeneration={marker?.SelectionGeneration}; request={marker?.RequestId}; "
            + $"markerIssued={marker?.WindowSequence}; markerClosed={marker?.ClosedWindowWatermark}; "
            + $"observedIssued={observedIssued}; observedClosed={observedClosed}; "
            + $"subcondition={FirstLogTimingSubcondition(marker, observedIssued, observedClosed)}; reason={error}.";
        if (observed || deferred) Plugin.Logger.LogInfo(diagnostic);
        else Plugin.Logger.LogWarning(diagnostic);
    }

    private static string FirstLogTimingSubcondition(
        ManagedSelectionLogMarker? marker, long observedIssued, long observedClosed)
    {
        if (marker == null) return "no-data-list-marker";
        if (marker.ClosedWindowWatermark < 0) return "marker-closed-watermark-negative";
        if (marker.WindowSequence <= marker.ClosedWindowWatermark) return "no-open-window-at-data-list";
        if (observedIssued != marker.WindowSequence) return "issued-window-changed-after-data-list";
        if (observedClosed < marker.ClosedWindowWatermark) return "closed-watermark-regressed";
        if (observedClosed >= observedIssued) return "observed-window-already-closed";
        return "same-data-list-open-window";
    }

    private static EditorPreviewSceneAddress CoreScene(SceneAddress scene) => new(
        scene.ProjectKey, scene.NodeGuid, scene.SceneIndex, scene.Fingerprint);

    private sealed class ManagedUnityLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.Message;
        public void LogEvent(object sender, LogEventArgs eventArgs) => Capture(eventArgs);
        public void Dispose() { }
    }
}
