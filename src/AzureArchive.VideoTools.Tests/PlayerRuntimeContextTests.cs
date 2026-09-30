using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Tests;

internal static class PlayerRuntimeContextTests
{
    public static void ClassifiesPreviewAndPlayback()
    {
        PlayerRuntimeContextSnapshot preview =
            PlayerRuntimeContextSnapshot.FromPreviewMode(true);
        PlayerRuntimeContextSnapshot playback =
            PlayerRuntimeContextSnapshot.FromPreviewMode(false);

        AssertEx.True(preview.PlayerAvailable);
        AssertEx.True(preview.PreviewMode);
        AssertEx.Equal(PlayerRuntimeMode.EditorPreview, preview.Mode);
        AssertEx.True(playback.PlayerAvailable);
        AssertEx.False(playback.PreviewMode);
        AssertEx.Equal(PlayerRuntimeMode.Playback, playback.Mode);
    }

    public static void ManualControlsArePlaybackOnlyAndFailClosed()
    {
        AssertEx.False(
            PlayerManualControlPolicy.AllowsManualControls(
                PlayerRuntimeContextSnapshot.Unavailable));
        AssertEx.False(
            PlayerManualControlPolicy.AllowsManualControls(
                PlayerRuntimeContextSnapshot.FromPreviewMode(true)));
        AssertEx.True(
            PlayerManualControlPolicy.AllowsManualControls(
                PlayerRuntimeContextSnapshot.FromPreviewMode(false)));
    }
}
