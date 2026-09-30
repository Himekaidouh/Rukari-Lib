namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Observation-stage discriminator for preview-chain application. The host
/// plugin owns the full <c>PlayerCommandObservationStage</c> enum as an
/// internal plugin type, so Core cannot name it; this policy only needs the
/// single distinction that governs chain application, and the runtime call
/// site maps every non-dispatch stage onto one explicit value.
/// </summary>
public enum PreviewChainApplyStage
{
    /// <summary>
    /// Any observation stage other than the validated scene-window dispatch
    /// stage (prefix-only, window capture, identity-only, would-dispatch,
    /// queue-no-op, both canaries, playback context probe, disabled).
    /// </summary>
    NotSceneCommandDispatch = 0,

    /// <summary>The validated scene-window command dispatch stage.</summary>
    SceneCommandDispatch = 1
}

/// <summary>
/// FIX E policy: decides whether a resolved preview chain must be applied
/// before the current scene's commands are dispatched.
/// </summary>
/// <remarks>
/// <para>
/// The runtime mode (editor preview vs formal playback) is deliberately NOT
/// an input. Chain application is capability+stage scoped: both editor
/// previews and formal playback resolve the same verified AAP graph and apply
/// the same idempotent inherited starts (duration=0 prepositioning from the
/// official origin), so the mode may only select execution plumbing — which
/// scene identity, dispatcher source, and schedule are used — never WHETHER
/// the resolved inheritance rebuild runs.
/// </para>
/// <para>
/// Gating on mode was exactly the defect this policy removes: formal playback
/// resolved the chain for lineage/reset metadata but never executed it, so a
/// direct entry into a scene started from leftover or official default
/// transforms instead of rebuilding the inherited start state.
/// </para>
/// </remarks>
public static class PreviewChainApplyPolicy
{
    /// <summary>
    /// True when the resolved preview chain must be applied before the
    /// current scene's commands. EditorPreview and Playback both qualify at
    /// the SceneCommandDispatch stage with the capability enabled; every
    /// other stage, and any state with the capability disabled, returns
    /// false.
    /// </summary>
    public static bool ShouldApplyChain(
        PreviewChainApplyStage stage,
        bool previewChainEnabled) =>
        stage == PreviewChainApplyStage.SceneCommandDispatch
        && previewChainEnabled;
}
