using System;
using System.Reflection;
using AzureArchive.VideoTools.Api;
using HarmonyLib;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Runtime;

internal static class EditorSelectionEventProbe
{
    private static int _callbackCount;
    internal static bool IsInstalled { get; private set; }

    public static void Install()
    {
        MethodInfo? target = AccessTools.Method(
            typeof(ScriptNodeInspector),
            nameof(ScriptNodeInspector.OnChildSelect),
            new[] { typeof(Selectable) });
        MethodInfo? postfix = AccessTools.Method(
            typeof(EditorSelectionEventProbe),
            nameof(Postfix),
            Type.EmptyTypes);

        if (target == null || postfix == null)
        {
            Plugin.Logger.LogError(
                "Selection-event probe was not installed: exact target or postfix was not found.");
            return;
        }

        try
        {
            Harmony harmony = new(Plugin.Guid + ".selection-event-probe");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            IsInstalled = true;
            Plugin.Logger.LogInfo(
                "Selection-event probe installed on ScriptNodeInspector.OnChildSelect(Selectable); "
                + "postfix receives zero arguments.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Selection-event probe installation failed: {ex}");
        }
    }

    public static void Postfix()
    {
        SharedModIntegration.InvalidateSelection();
        _callbackCount++;
        ManagedUnityLogSelectionProbe.SelectionCorrelation correlation =
            ManagedUnityLogSelectionProbe.CompleteSelection();
        Plugin.Host.CapabilitiesInternal.Verified(
            "Editor.SelectionEvent",
            "OnChildSelect zero-argument postfix executed; no game object was received or read");
        Plugin.Logger.LogInfo(
            $"Selection-event zero-argument callback count={_callbackCount}; no game object was received or read.");

        if (!correlation.HasMarker)
        {
            Plugin.Logger.LogWarning(
                "Managed-log selection correlation has no preceding DataList marker.");
            return;
        }

        if (correlation.MessagesAfterDataList == 0)
        {
            Plugin.Logger.LogWarning(
                $"Managed-log selection correlation request={correlation.RequestId}; "
                + "no managed Unity string arrived after DataList.");
            return;
        }

        Plugin.Host.CapabilitiesInternal.Verified(
            "Editor.ManagedLogSelection",
            "A managed Unity string was correlated between DataList and OnChildSelect");
        EditorSceneIdentitySnapshot identity = Plugin.Host.SceneIdentityInternal.Publish(
            correlation.RequestId,
            correlation.First.Hash,
            correlation.First.Length,
            correlation.First.LineCount);
        ApiResult<SceneSnapshot> selectedScene = Plugin.Api.Editor.GetSelectedScene();
        // Demoted bind point: the selection event only CONFIRMS the open
        // generation's scene. It never binds a lease; authorization happens
        // exclusively in EmbeddedEditorPreviewLeaseCache.TryClaim.
        EmbeddedEditorPreviewLeaseCache.RefreshGenerationScene(identity, selectedScene);
        Plugin.Host.SceneResolutionInternal.Enqueue(identity);
        Plugin.Logger.LogInfo(
            $"Managed-log selection correlation request={correlation.RequestId}; "
            + $"unitySeq={correlation.StartSequence}->{correlation.EndSequence}; "
            + $"messages={correlation.MessagesAfterDataList}; "
            + $"candidateLen={correlation.First.Length}; candidateLines={correlation.First.LineCount}; "
            + $"candidateSha16={correlation.First.Hash.Substring(0, 16)}; candidatePreview={correlation.First.Preview}; "
            + $"tailSha16={correlation.Latest.Hash.Substring(0, 16)}; tailPreview={correlation.Latest.Preview}");
        Plugin.Logger.LogInfo(
            $"Scene identity API updated: sequence={identity.ObservationSequence}; "
            + $"request={identity.SelectionRequestId}; sha256={identity.CompiledScriptSha256}; "
            + $"length={identity.CompiledScriptLength}; lines={identity.CompiledScriptLineCount}; "
            + "source=managed-log-first-message.");
    }
}
