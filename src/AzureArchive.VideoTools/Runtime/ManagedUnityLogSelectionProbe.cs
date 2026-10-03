using System;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Commands;
using BepInEx.Logging;
using Rukari.Lib;

namespace AzureArchive.VideoTools.Runtime;

internal static class ManagedUnityLogSelectionProbe
{
    private static readonly object Gate = new();
    private static readonly ManagedSelectionLogCorrelation Correlation = new();
    private static ManagedUnityLogListener? _listener;
    private static long _sequence;
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
        EditorInputSceneCaptureProof? inputProof)
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
            Correlation.Begin(requestId, _sequence, mainThreadId,
                PlayerAdvanceObservationWindow.IssuedSequence,
                PlayerAdvanceObservationWindow.ClosedSequenceWatermark, scene);
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

    private static void Capture(LogEventArgs eventArgs)
    {
        if (!string.Equals(eventArgs.Source.SourceName, "Unity", StringComparison.Ordinal)
            || eventArgs.Data is not string text) return;
        string error;
        lock (Gate)
        {
            long sequence = ++_sequence;
            CompiledScriptIdentity? identity = text.Length <= ManagedSelectionLogCorrelation.MaximumScriptCharacters
                ? CommandIdentity.CompiledScript(text) : null;
            if (Correlation.TryObserveFirst(sequence, identity, Environment.CurrentManagedThreadId,
                    PlayerAdvanceObservationWindow.IssuedSequence,
                    PlayerAdvanceObservationWindow.ClosedSequenceWatermark, out error)) return;
        }

        if (error is "no-data-list-marker" or "first-log-already-observed") return;
        Plugin.Logger.LogWarning($"Managed-log first selection message rejected: reason={error}.");
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
