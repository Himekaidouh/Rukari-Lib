using Rukari.Lib;
using Rukari.Lib.Tools;

namespace UiToolboxClient;

/// <summary>
/// A contract-only example. A real BepInEx plugin calls RegisterAsync after Rukari lib is ready,
/// retains the returned instance, and calls Dispose on the game main thread during shutdown.
/// Its own package declares a hard dependency on rukari.lib.runtime; it does not ship Rukari.Lib.dll.
/// </summary>
public sealed class UiToolboxClient : IDisposable
{
    private const string OwnerId = "example.rukari.ui";
    private const string ListPageId = "example.rukari.ui.list";
    private const string HostedPageId = "example.rukari.ui.hosted";

    private readonly IModRuntime _runtime;
    private readonly IToolboxService _toolbox;
    private readonly HostedPage _hosted = new();
    private IDisposable? _listLease;
    private IDisposable? _hostedLease;
    private int _clicks;
    private string? _selectedItem;

    private UiToolboxClient(IModRuntime runtime, IToolboxService toolbox)
    {
        _runtime = runtime;
        _toolbox = toolbox;
    }

    public static Task<ModResult<UiToolboxClient>> RegisterAsync(CancellationToken cancellation = default)
    {
        IModRuntime? runtime = ModServices.Current;
        if (runtime is null || runtime.State != RuntimeState.Ready)
            return Task.FromResult(ModResult<UiToolboxClient>.Fail(ModErrorCode.NotReady, "Rukari lib runtime is not ready."));

        // Discovery is managed and thread-safe. A missing/disabled capability is an expected state,
        // for example when the native input guard could not be installed for this game build.
        ModResult<CapabilityInfo> capability = runtime.GetCapability(ToolCapabilities.Toolbox);
        if (!capability.Success)
            return Task.FromResult(ModResult<UiToolboxClient>.Fail(capability.Error!));
        if (capability.Value.Level == CapabilityLevel.Unavailable)
            return Task.FromResult(ModResult<UiToolboxClient>.Fail(ModErrorCode.Unsupported, capability.Value.Detail));

        // RegisterPage/RegisterHostedPage require the game main thread. Look up the service inside
        // the operation rather than retaining one across a queued dispatch or runtime replacement.
        return runtime.InvokeAsync(() =>
        {
            ModResult<IToolboxService> service = runtime.GetService<IToolboxService>();
            if (!service.Success) return ModResult<UiToolboxClient>.Fail(service.Error!);

            var client = new UiToolboxClient(runtime, service.Value);
            ModResult<IDisposable> list = service.Value.RegisterPage(
                OwnerId, ListPageId, "UI 示例 · 列表", client.Snapshot, client.Handle, "card");
            if (!list.Success) return ModResult<UiToolboxClient>.Fail(list.Error!);
            client._listLease = list.Value;

            // Both pages have one ownerId, so the toolbox gives them one shared rail button.
            ModResult<IDisposable> hosted = service.Value.RegisterHostedPage(
                OwnerId, HostedPageId, "UI 示例 · 内容", client._hosted, "card");
            if (!hosted.Success)
            {
                client.Dispose(); // Roll back the first registration; leave no orphan page.
                return ModResult<UiToolboxClient>.Fail(hosted.Error!);
            }
            client._hostedLease = hosted.Value;
            return ModResult<UiToolboxClient>.Ok(client);
        }, cancellation);
    }

    private ToolPageSnapshot Snapshot() => new(
        Summary: "Rukari lib 负责侧栏、窗口和列表；示例只提供内容与动作。",
        Buttons: new[]
        {
            new ToolButton("increment", "计数 +1"),
            new ToolButton("reset", "清零", Enabled: _clicks > 0)
        },
        Items: new[]
        {
            new ToolListItem("a", "示例资源 A", Selected: _selectedItem == "a"),
            new ToolListItem("b", "示例资源 B", Selected: _selectedItem == "b")
        },
        Status: $"点击 {_clicks} 次；选中 {_selectedItem ?? "无"}",
        ItemActionId: "select");

    private void Handle(ToolAction action)
    {
        switch (action.Id)
        {
            case "increment": _clicks++; break;
            case "reset": _clicks = 0; break;
            case "select" when action.Value is "a" or "b": _selectedItem = action.Value; break;
            default: return;
        }
        // Snapshot state belongs to this provider; ask the host to read it again after a change.
        _toolbox.Refresh(ListPageId);
    }

    /// <summary>Call on the game main thread when the owning plugin stops.</summary>
    public void Dispose()
    {
        if (!_runtime.IsMainThread)
            throw new InvalidOperationException("Dispose toolbox page leases on the game main thread.");
        Interlocked.Exchange(ref _hostedLease, null)?.Dispose();
        Interlocked.Exchange(ref _listLease, null)?.Dispose();
    }

    private sealed class HostedPage : IToolPanelContent, IToolPanelSizing
    {
        private int _clicks;

        public float PreferredHeight => 190f;
        public float PreferredWidth => 420f;

        public void Draw(IToolPanelSurface surface)
        {
            // The surface is panel-local and already scaled; Y starts at the bottom.
            // Row() grows upward, so reserve the status/footer before the controls.
            ToolInputRect footer = surface.Row(34f);
            ToolInputRect buttons = surface.Row(42f);
            ToolInputRect title = surface is IToolPanelSurfaceTopDown topDown
                ? topDown.Band(40f)
                : new ToolInputRect(0f, Math.Max(0f, surface.Height - 40f), surface.Width, 40f);

            surface.Plate("demo.title.plate", title, ToolSurfaceStyle.Header);
            surface.Text("demo.title", "由 lib 绘制的托管内容页", title, size: 18);
            if (DrawButton(surface, "demo.add", "增加", surface.Cell(buttons, 0, 2),
                    ToolSurfaceStyle.Primary, enabled: true)) _clicks++;
            if (DrawButton(surface, "demo.reset", "重置", surface.Cell(buttons, 1, 2),
                    ToolSurfaceStyle.Undo, enabled: _clicks > 0))
                _clicks = 0;
            surface.Status("demo.status", $"本页点击 {_clicks} 次", footer);
        }

        private static bool DrawButton(IToolPanelSurface surface, string id, string label,
            ToolInputRect bounds, ToolSurfaceStyle style, bool enabled) =>
            (surface as IToolPanelSurfaceStyledButtons)?.StyledButton(
                id, label, bounds, style, enabled, highlighted: false)
            ?? surface.Button(id, label, bounds, enabled: enabled);
    }
}
