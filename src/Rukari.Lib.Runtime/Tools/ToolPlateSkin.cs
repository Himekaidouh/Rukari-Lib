extern alias unitycore;
extern alias unityui;

using Rukari.Lib.Tools;
using Color = unitycore::UnityEngine.Color;
using Image = unityui::UnityEngine.UI.Image;

namespace Rukari.Lib.Runtime.Tools;

/// <summary>
/// The face every shared button wears: the game's own rounded background plate, nine-sliced and tinted, so a small
/// button looks like the rest of AzureArchive instead of a flat pastel rectangle.
///
/// The plate is white, and that is what makes three states one sprite: normal, selected and disabled differ only
/// by the tint, so the label keeps the same dark foreground in every state. Choosing the plate and its text colour
/// together, in one place, is the whole point of this type — choosing them apart is exactly what produced an
/// unreadable button (dark text on the game's dark navy button plate).
/// </summary>
internal static class ToolPlateSkin
{
    private const string RowPlate = "row";

    /// <summary>
    /// Applies the shared plate and returns the label colour that belongs with it, so the two cannot drift apart.
    /// The fallback returns the same kind of foreground, which keeps the caller free of any branch: a machine
    /// without the decoded atlas export still gets a readable button.
    /// </summary>
    internal static Color Apply(Image image, bool enabled, bool highlighted, float canvasScale)
    {
        string skin = !enabled ? "button.disabled" : highlighted ? "button.selected" : "button";
        ToolColorPair pair = ToolPalette.ForKey(skin);
        var sprite = AtlasEmblemSource.GetPlate(RowPlate);
        image.enabled = true;
        image.preserveAspect = false;
        if (sprite is null)
        {
            // No atlas: the shared procedural theme draws the same idea from a generated rounded sprite.
            if (!ToolTheme.Apply(image, skin)) image.color = ToolTheme.Native(pair.Background);
            return ToolTheme.Native(pair.Foreground);
        }
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.color = ToolTheme.Native(pair.Background);
        // Keep the plate's corner the same size on screen at every panel scale: the drawn rectangle is divided by
        // the canvas scale, so the slice has to follow it or the corner grows into a pill on a small window.
        if (canvasScale > 0f && !float.IsNaN(canvasScale)) image.pixelsPerUnitMultiplier = canvasScale;
        return ToolTheme.Native(pair.Foreground);
    }

    /// <summary>
    /// Minimum height a button needs before Unity starts clamping the plate's border and the corner grows into a
    /// semicircle. The plate is 24 tall with a 10 pixel border, so this is a comfortable margin.
    /// </summary>
    internal const float MinimumPlateHeight = ToolDrawerLayout.OfficialPlateMinimumHeight;
}
