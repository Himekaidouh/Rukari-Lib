using AzureArchive.VideoTools.Core.Commands;
using Rukari.Lib.Editor;
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
        RegisterPages(tools.Value);
    }

    internal static void RegisterPages(IToolboxService tools)
    {
        // The editor stays in the drawer: it is tied to the selected line and to the story canvas beside it.
        var result = tools.RegisterHostedPage(
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

        // The preset page draws in its own movable window when the library publishes one, so it can stay on screen
        // beside the editor instead of replacing it. Registration happens at load time, so a library without a
        // window host — or one whose window could not be built — simply keeps the drawer page it has always had.
        IDisposable? presets = RegisterToolPage(tools, Plugin.Guid + ".presets", "rukari.moreeffects.presets",
            "预设效果", static () => new CharacterPresetGroupContent(), "wave");
        if (presets is not null) Leases.Add(presets);
    }

    /// <summary>
    /// One tool page of this mod: a standalone window when the installed library offers one, and the shared drawer
    /// sheet otherwise. Both paths take the same content type, so nothing about the page itself changes, and a
    /// library that cannot build the window says so at registration time rather than when the user clicks.
    /// </summary>
    private static IDisposable? RegisterToolPage(IToolboxService tools, string ownerId, string pageId,
        string title, Func<IToolPanelContent> create, string iconId)
    {
        var windows = ModServices.Current?.GetService<IToolWindowService>();
        if (windows?.Success == true && windows.Value is not null)
        {
            var window = windows.Value.RegisterWindow(ownerId, pageId, title, create(), iconId);
            if (window.Success && window.Value is not null) return window.Value;
            Plugin.Logger.LogWarning($"Tool window {pageId} was not registered: {window.Error}");
        }
        var page = tools.RegisterHostedPage(ownerId, pageId, title, create(), iconId);
        if (page.Success) return page.Value;
        Plugin.Logger.LogWarning($"Tool page {pageId} was not registered: {page.Error}");
        return null;
    }

    internal static void Stop()
    {
        foreach (IDisposable lease in Leases) lease.Dispose();
        Leases.Clear();
    }
}

// The legacy correlation probes remain stage-specific (compiled scene identity/preview lease).
// The existing DataList observer notifies the optional managed token-invalidation capability.
internal static class SharedModIntegration
{
    internal static void InvalidateSelection()
    {
        try
        {
            var documents = ModServices.Current?.GetService<IEditorDocumentService>();
            var invalidation = documents?.Success == true
                ? documents.Value as IEditorSelectionInvalidation : null;
            if (invalidation == null) return;
            var result = invalidation.InvalidateSelection();
            if (!result.Success)
                Plugin.Logger.LogWarning($"Shared selection invalidation rejected: {result.Error?.Code}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Shared selection invalidation failed: {ex.GetType().Name}.");
        }
    }
}
