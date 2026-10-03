using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// Managed selection confirmation only. The native Selectable callback is
/// blacklisted: this class has no Harmony install method or native callback.
/// </summary>
internal static class EditorSelectionEventProbe
{
    internal static void PublishManagedLog(
        ManagedSelectionLogCandidate candidate,
        ApiResult<SceneSnapshot> selectedScene)
    {
        // The caller has already consumed the current marker, checked the
        // complete captured/live scene address and kept this on the main thread.
        EditorSceneIdentitySnapshot identity = Plugin.Host.SceneIdentityInternal.Publish(
            candidate.Marker.RequestId, candidate.Script.Sha256,
            candidate.Script.Utf16Length, candidate.Script.LineCount);
        EmbeddedEditorPreviewLeaseCache.RefreshGenerationScene(identity, selectedScene);
        Plugin.Host.SceneResolutionInternal.Enqueue(identity);
        Plugin.Logger.LogInfo(
            $"Scene identity API updated: sequence={identity.ObservationSequence}; "
            + $"generation={candidate.Marker.Generation}; request={identity.SelectionRequestId}; "
            + $"sha256={identity.CompiledScriptSha256}; length={identity.CompiledScriptLength}; "
            + $"lines={identity.CompiledScriptLineCount}; unitySeq={candidate.LogSequence}; "
            + $"window={candidate.Marker.WindowSequence}; "
            + "source=managed-log-first-message-data-list-main-thread-pump; "
            + "sceneReverified=true; executionAuthorized=false.");
    }
}
