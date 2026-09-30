using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Tests;

internal static class AutoDialogueDelayTests
{
    public static void ReplacesOnlyOfficialTwoSecondRuntimeTier()
    {
        AssertEx.Equal(
            2.5f,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(2f));
        AssertEx.Equal(
            2.5f,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(2.0005f));

        AssertEx.Equal(
            0f,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(0f));
        AssertEx.Equal(
            1f,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(1f));
        AssertEx.Equal(
            3f,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(3f));
        AssertEx.Equal(
            4f,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(4f));
        AssertEx.True(float.IsNaN(
            AutoDialogueDelayPolicy.MapRuntimeSeconds(float.NaN)));
        AssertEx.Equal(
            float.PositiveInfinity,
            AutoDialogueDelayPolicy.MapRuntimeSeconds(float.PositiveInfinity));
    }
}
