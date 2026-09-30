namespace AzureArchive.VideoTools.Core.VisualEditor;

public readonly record struct VisualEditorInputRegion(
    bool Visible,
    bool Expanded,
    bool Dragging,
    float CanvasScale,
    VisualEditorRect Panel,
    VisualEditorRect Handle);

public static class VisualEditorInputGuardMath
{
    public static bool ShouldSuppressOfficialMouse(
        VisualEditorInputRegion region,
        VisualEditorPoint screenPoint)
    {
        if (!region.Visible)
        {
            return false;
        }

        if (region.Dragging)
        {
            return true;
        }

        if (!screenPoint.IsFinite
            || !float.IsFinite(region.CanvasScale)
            || region.CanvasScale <= 0f)
        {
            return false;
        }

        var canvasPoint = new VisualEditorPoint(
            screenPoint.X / region.CanvasScale,
            screenPoint.Y / region.CanvasScale);
        if (Contains(region.Handle, canvasPoint))
        {
            return true;
        }

        return region.Expanded && Contains(region.Panel, canvasPoint);
    }

    private static bool Contains(VisualEditorRect rect, VisualEditorPoint point) =>
        rect.IsValid
        && point.X >= rect.X
        && point.X <= rect.X + rect.Width
        && point.Y >= rect.Y
        && point.Y <= rect.Y + rect.Height;
}
