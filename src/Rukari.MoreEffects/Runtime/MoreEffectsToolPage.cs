using Rukari.Lib;
using Rukari.Lib.Tools;

namespace AzureArchive.VideoTools.Runtime;

internal static class MoreEffectsToolPage
{
    private static readonly List<IDisposable> Leases = new();

    internal static void Install()
    {
        Stop();
        var tools = ModServices.Current?.GetService<IToolboxService>();
        if (tools?.Success != true || tools.Value == null) return;
        // A hosted page: the shared panel draws the frame, the title, the close button and every input region, and
        // this mod only fills the content rectangle with the editor's own controls. The page used to be a snapshot
        // with one "打开画面编辑器" button, and the editor then built a second, competing panel of its own.
        var result = tools.Value.RegisterHostedPage(
            Plugin.Guid,
            "rukari.moreeffects.editor",
            "更多的画面效果",
            new VisualEditorHostedContent(),
            iconId: "camera");
        if (result.Success)
        {
            if (result.Value != null) Leases.Add(result.Value);
        }
        else Plugin.Logger.LogWarning($"Visual editor tool page was not registered: {result.Error}");

        // Only the editor and the preset group are public leaves. The group owns choosing an effect
        // and returning from its parameters inside the same shared sheet.
        var presets = tools.Value.RegisterHostedPage(Plugin.Guid,
            "rukari.moreeffects.presets", "预设效果", new CharacterPresetGroupContent(), iconId: "wave");
        if (presets.Success && presets.Value != null) Leases.Add(presets.Value);
        else Plugin.Logger.LogWarning($"Character preset group was not registered: {presets.Error}");
    }

    internal static void Stop()
    {
        foreach (IDisposable lease in Leases) lease.Dispose();
        Leases.Clear();
    }
}

// The legacy correlation probes remain stage-specific (compiled scene identity/preview lease).
// The common editor transaction service invalidates its own tokens through its single prefix pair.
internal static class SharedModIntegration
{
    internal static void InvalidateSelection() { }
}
