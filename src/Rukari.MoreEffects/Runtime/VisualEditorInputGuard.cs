using AzureArchive.VideoTools.Core.VisualEditor;
using Rukari.Lib;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

internal static class VisualEditorInputGuard
{
    private static IToolInputRegion? _lease;
    public static bool Install()
    {
        var service = ModServices.Current?.GetService<IToolInputService>();
        if (service?.Success != true || service.Value == null) return false;
        var registration = service.Value.RegisterRegion(Plugin.Guid, "visual-editor");
        _lease = registration.Value;
        return registration.Success;
    }
    public static void Publish(VisualEditorInputRegion region)
    {
        if (!region.Visible || !float.IsFinite(region.CanvasScale) || region.CanvasScale <= 0)
        { _lease?.Update(Array.Empty<ToolInputRect>()); return; }
        float scale = region.CanvasScale;
        ToolInputRect Convert(VisualEditorRect r) => new(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
        _lease?.Update(region.Expanded ? new[] { Convert(region.Handle), Convert(region.Panel) }
            : new[] { Convert(region.Handle) }, region.Dragging);
    }
    public static void Clear() => _lease?.Update(Array.Empty<ToolInputRect>());
}
