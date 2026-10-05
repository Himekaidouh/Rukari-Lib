using AzureArchive.VideoTools.Core.Cameras;

namespace AzureArchive.VideoTools.Api;

/// <summary>Internal sibling contract; existing public camera dispatch signatures remain frozen.</summary>
internal interface ISceneCameraMutationEvidenceDispatcher
{
    ApiResult<SceneCameraScopedInheritedStartSnapshot> ApplyInheritedStartWithFailureEvidenceOnMainThread(
        string sceneIdentity,
        SceneCameraComposition inheritedState,
        bool hasOverall,
        bool hasBackground,
        out SceneCameraMutationFailureEvidence failure);
}
