using System.Reflection;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class PreviewChainApplyPolicyTests
{
    // FIX E contract: chain application is capability+stage scoped and
    // deliberately mode-independent. EditorPreview AND formal playback both
    // qualify at the SceneCommandDispatch stage; the runtime mode may only
    // select execution plumbing, never whether the resolved inheritance
    // rebuild runs.

    public static void SceneCommandDispatchWithCapabilityAppliesChain()
    {
        AssertEx.True(
            PreviewChainApplyPolicy.ShouldApplyChain(
                PreviewChainApplyStage.SceneCommandDispatch,
                previewChainEnabled: true),
            "SceneCommandDispatch + PreviewChainEnabled must apply the chain.");
    }

    public static void OtherStagesNeverApplyChainEvenWhenEnabled()
    {
        AssertEx.False(
            PreviewChainApplyPolicy.ShouldApplyChain(
                PreviewChainApplyStage.NotSceneCommandDispatch,
                previewChainEnabled: true),
            "Every non-dispatch observation stage must never apply the chain.");
    }

    public static void DisabledCapabilityNeverAppliesChain()
    {
        AssertEx.False(
            PreviewChainApplyPolicy.ShouldApplyChain(
                PreviewChainApplyStage.SceneCommandDispatch,
                previewChainEnabled: false),
            "A disabled preview-chain capability must never apply the chain.");
        AssertEx.False(
            PreviewChainApplyPolicy.ShouldApplyChain(
                PreviewChainApplyStage.NotSceneCommandDispatch,
                previewChainEnabled: false));
    }

    public static void PolicySignatureIsModeIndependentByDesign()
    {
        // Guard against reintroducing a mode parameter: the public decision
        // surface must stay exactly (stage, previewChainEnabled). The stage
        // discriminator is a pure Core enum because the full
        // PlayerCommandObservationStage enum is an internal plugin type that
        // Core cannot name.
        MethodInfo method = AssertEx.NotNull(
            typeof(PreviewChainApplyPolicy).GetMethod(
                nameof(PreviewChainApplyPolicy.ShouldApplyChain)),
            "ShouldApplyChain must remain a public static method.");
        ParameterInfo[] parameters = method.GetParameters();
        AssertEx.Equal(2, parameters.Length);
        AssertEx.Equal(typeof(PreviewChainApplyStage), parameters[0].ParameterType);
        AssertEx.Equal(typeof(bool), parameters[1].ParameterType);
        AssertEx.Equal(typeof(bool), method.ReturnType);
    }
}
