using AzureArchive.VideoTools.Core.Characters;
using BepInEx.Configuration;

namespace AzureArchive.VideoTools.Runtime;

internal sealed record VisualEditorOptions(
    bool Enabled,
    bool StartExpanded,
    int DraftDurationMilliseconds,
    CharacterTransformEasing DraftEasing)
{
    public static VisualEditorOptions Bind(ConfigFile config)
    {
        ConfigEntry<bool> enabled = config.Bind(
            "VisualEditor",
            "Enabled",
            true,
            "Enable the guarded AAVT visual command editor in editor preview mode.");
        ConfigEntry<bool> startExpanded = config.Bind(
            "VisualEditor",
            "StartExpanded",
            false,
            "Start the visual command editor drawer expanded.");
        ConfigEntry<int> duration = config.Bind(
            "VisualEditor",
            "DraftDurationMilliseconds",
            600,
            "Default duration written to drag-generated character commands.");
        ConfigEntry<CharacterTransformEasing> easing = config.Bind(
            "VisualEditor",
            "DraftEasing",
            CharacterTransformEasing.EaseInOut,
            "Default easing written to drag-generated draft commands.");

        return new VisualEditorOptions(
            enabled.Value,
            startExpanded.Value,
            Math.Max(0, duration.Value),
            easing.Value);
    }
}
