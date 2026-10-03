using System;
using System.Reflection;
using AzureArchive.VideoTools.Api;
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
        EmbeddedEditorPreviewLeaseCache.BeginPreviewGeneration(
            index,
            capturedScene,
            PlayerAdvanceObservationWindow.ClosedSequenceWatermark);
        // This primitive is an opaque official request value, not a scene index.
        // Keep the already captured managed scene for later full live re-verification.
        ManagedUnityLogSelectionProbe.MarkDataList(index, capturedScene, inputProof);
        Plugin.Host.CapabilitiesInternal.Verified(
            "Editor.DataListEvent",
            "ScriptNodeInspector.DataList index reached the postfix; no instance or result was received");
        Plugin.Logger.LogInfo(
            $"Editor DataList index callback count={_callbackCount}; index={index}; "
            + $"captureSource={(inputProof == null ? "selected-row" : "input-unique-visible-row")}; "
            + $"capture={(capturedScene.Success ? "ok" : capturedScene.Error)}; no instance or result was received.");
    }
}
