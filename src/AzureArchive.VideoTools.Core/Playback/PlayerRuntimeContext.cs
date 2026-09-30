namespace AzureArchive.VideoTools.Core.Playback;

public enum PlayerRuntimeMode
{
    Unavailable = 0,
    Playback = 1,
    EditorPreview = 2
}

public sealed record PlayerRuntimeContextSnapshot(
    bool PlayerAvailable,
    bool PreviewMode,
    PlayerRuntimeMode Mode)
{
    public static PlayerRuntimeContextSnapshot Unavailable { get; } = new(
        false,
        false,
        PlayerRuntimeMode.Unavailable);

    public static PlayerRuntimeContextSnapshot FromPreviewMode(bool previewMode) => new(
        true,
        previewMode,
        previewMode ? PlayerRuntimeMode.EditorPreview : PlayerRuntimeMode.Playback);
}

public static class PlayerManualControlPolicy
{
    public static bool AllowsManualControls(PlayerRuntimeContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.PlayerAvailable
            && context.Mode == PlayerRuntimeMode.Playback
            && !context.PreviewMode;
    }
}
