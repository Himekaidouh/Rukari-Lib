using System;
using System.Reflection;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Interop;
using HarmonyLib;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Runtime;

internal static class EditorDataListEventProbe
{
    private static int _callbackCount;
    internal static bool IsInstalled { get; private set; }

    public static void Install()
    {
        MethodInfo? target = AccessTools.Method(
            typeof(ScriptNodeInspector),
            nameof(ScriptNodeInspector.DataList),
            new[] { typeof(int) });
        MethodInfo? postfix = AccessTools.Method(
            typeof(EditorDataListEventProbe),
            nameof(Postfix),
            new[] { typeof(int) });

        if (target == null || postfix == null)
        {
            Plugin.Logger.LogError(
                "Editor DataList probe was not installed: exact target or postfix was not found.");
            return;
        }

        try
        {
            Harmony harmony = new(Plugin.Guid + ".editor-data-list-probe");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            IsInstalled = true;
            Plugin.Logger.LogInfo(
                "Editor DataList probe installed on ScriptNodeInspector.DataList(Int32); "
                + "postfix receives only the original Int32 index argument.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Editor DataList probe installation failed: {ex}");
        }
    }

    public static void Postfix(int index)
    {
        SharedModIntegration.InvalidateSelection();
        _callbackCount++;
        // Main-thread postfix: capture the selected scene FIRST so the new
        // generation is born with a managed snapshot of exactly what the
        // editor showed when the row was opened.
        ApiResult<SceneSnapshot> capturedScene = Plugin.Host.EditorInternal.CaptureDataListScene(out var inputProof);
        // C feature: a DataList means the editor's scene selection changed —
        // mark the live editor graph stale for the next debounced rebuild.
        LiveProjectGraphService.Invalidate("data-list");
        // Eligibility floor is the CLOSED watermark, not IssuedSequence:
        // on device DataList fires mid-preview-cascade, after this click's
        // advance windows already OPENED, so IssuedSequence would make every
        // window of the current click look "stale". Windows that were still
        // open when the generation began remain claimable; everything that
        // fully closed before it stays rejected.
        var window = PlayerAdvanceObservationWindow.ReadLifecycleSnapshot();
        var runtime = Rukari.Lib.ModServices.Current;
        int mainThreadId = runtime is { State: Rukari.Lib.RuntimeState.Ready, IsMainThread: true }
            ? Environment.CurrentManagedThreadId : 0;
        var scene = capturedScene.Success && capturedScene.Value != null
            ? new EditorPreviewSceneAddress(capturedScene.Value.Address.ProjectKey,
                capturedScene.Value.Address.NodeGuid, capturedScene.Value.Address.SceneIndex,
                capturedScene.Value.Address.Fingerprint) : null;
        var cascade = new EditorDataListCascadeProof(index, mainThreadId,
            window.ActiveWindowSequence, window.IssuedWindowSequence,
            window.ClosedWindowWatermark, scene, inputProof);
        string markerReason = "cache-generation-unavailable";
        bool currentMarkerMatches = EmbeddedEditorPreviewLeaseCache.TryGetCurrentGeneration(out var scope)
            && ManagedUnityLogSelectionProbe.MatchesCurrentDataListMarker(scope.GenerationId, cascade, out markerReason);
        bool reused = EmbeddedEditorPreviewLeaseCache.BeginOrReusePreviewGeneration(
            index, capturedScene, cascade, currentMarkerMatches, out long selectionGeneration, out string cascadeReason);
        // This primitive is an opaque official request value, not a scene index.
        // Keep the already captured managed scene for later full live re-verification.
        // Exact duplicates preserve BOTH the cache generation and first-log
        // marker. Shared document tokens were still invalidated above.
        if (!reused)
            ManagedUnityLogSelectionProbe.MarkDataList(index, capturedScene, inputProof, selectionGeneration, window);
        Plugin.Host.CapabilitiesInternal.Verified(
            "Editor.DataListEvent",
            "ScriptNodeInspector.DataList index reached the postfix; no instance or result was received");
        Plugin.Logger.LogInfo(
            $"Editor DataList index callback count={_callbackCount}; index={index}; "
            + $"selectionGeneration={selectionGeneration}; cascade={(reused ? "reuse" : "new")}; "
            + $"activeWindow={window.ActiveWindowSequence}; cutoff={window.ClosedWindowWatermark}; reason={cascadeReason}; "
            + $"markerReason={markerReason}; sceneIndex={scene?.SceneIndex}; "
            + $"captureSource={(inputProof == null ? "selected-row" : "confirmed-scene-input-proof")}; "
            + $"capture={(capturedScene.Success ? "ok" : capturedScene.Error)}; no instance or result was received.");
    }
}
