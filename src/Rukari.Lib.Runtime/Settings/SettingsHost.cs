using Rukari.Lib.Settings;
using Rukari.Lib.Tools;
using Rukari.Lib.Runtime.Tools;

namespace Rukari.Lib.Runtime.Settings;

/// <summary>Global settings share the existing input guard, but not the editor drawer's visibility gate.</summary>
internal static class SettingsHost
{
    private const string OwnerId = "rukari.lib.runtime";
    private static OfficialSettingsEntry? _entry;
    private static SettingsWindow? _window;
    private static ModSettingsService? _service;
    private static IDisposable? _registration;
    private static IDisposable? _libPage;
    private static bool _failed;
    private static Action<string>? _log;

    internal static bool IsOpen => !_failed && _service?.IsOpen == true;

    internal static void Initialize(IModRuntime runtime, ToolInputService input, Action<string> log)
    {
        if (_service is not null) return;
        if (!runtime.IsMainThread) throw new InvalidOperationException("Settings initialization requires the main thread.");
        _log = log;
        _failed = false;
        // Opening a modal without complete keyboard ownership would let Escape/type input reach the parent.
        if (!input.SupportsKeyboardCapture)
        {
            log("Global mod settings unavailable: complete shared keyboard ownership is required.");
            return;
        }
        try
        {
            var service = new ModSettingsService(() => runtime.IsMainThread,
                () => ShowWindow(input, log), () => _window?.Hide(),
                (owner, ex) => log($"[mod-settings] Page '{owner}' failed: {ex.GetType().Name}: {ex.Message}"));
            _service = service;
            var registration = runtime.RegisterService<IModSettingsService>(OwnerId, service,
                new CapabilityInfo(SettingsCapabilities.Settings, OwnerId, Plugin.Version, CapabilityLevel.Experimental,
                    "Global managed settings pages opened from the official settings panel; native validation pending."));
            if (!registration.Success) throw new InvalidOperationException(registration.Error!.Message);
            _registration = registration.Value;
            var page = service.RegisterPage(OwnerId, "Rukari Lib", new LibrarySettingsPage());
            if (!page.Success) throw new InvalidOperationException(page.Error!.Message);
            _libPage = page.Value;
            _entry = new OfficialSettingsEntry(() =>
            {
                var result = service.Open(null);
                if (!result.Success) log($"[mod-settings] Open failed: {result.Error!.Message}");
            }, log);
            log("Global mod settings service registered; awaiting the official settings panel.");
        }
        catch (Exception ex)
        {
            Shutdown();
            log($"Global mod settings initialization failed safely: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ModResult<bool> ShowWindow(ToolInputService input, Action<string> log)
    {
        if (_failed || _entry?.IsSettingsVisible != true)
            return ModResult<bool>.Fail(ModErrorCode.NotReady, "Open the official settings panel before opening mod settings.");
        try
        {
            _window ??= new SettingsWindow(input, log);
            _window.Show();
            return ModResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _window?.Dispose();
            _window = null;
            return ModResult<bool>.Fail(ModErrorCode.ProviderFailed, "The mod settings window could not be created: " + ex.Message);
        }
    }

    internal static void Tick()
    {
        if (_failed || _service is null) return;
        try
        {
            _entry?.Tick();
            if (_entry?.IsSettingsVisible != true && _service.IsOpen) _service.Close();
            // Also pumps the single closing frame's input guard while hidden.
            _window?.Tick(_service);
        }
        catch (Exception ex)
        {
            _failed = true;
            _service.Close();
            _window?.Dispose();
            _window = null;
            _entry?.Dispose();
            _entry = null;
            _log?.Invoke($"[mod-settings] Disabled after a rendering failure: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void Shutdown()
    {
        _service?.Close();
        _libPage?.Dispose(); _libPage = null;
        _registration?.Dispose(); _registration = null;
        _service?.Dispose(); _service = null;
        _entry?.Dispose(); _entry = null;
        _window?.Dispose(); _window = null;
        _log = null;
        _failed = false;
    }

    private sealed class LibrarySettingsPage : IToolPanelContent
    {
        public float PreferredHeight => 400f;
        public void Draw(IToolPanelSurface surface)
        {
            var layout = (IToolPanelSurfaceTopDown)surface;
            surface.Text("title", "Rukari Lib", layout.Band(46f), 28);
            surface.Text("intro", "在左侧选择模组，查看它提供的全局设置。", layout.Band(62f), 21);
            surface.Status("empty", "已加载并接入设置功能的模组会显示在这里。", layout.Band(58f));
            surface.Status("stage", "这里只显示已接入的模组设置。播放界面主题将在后续版本接入。", layout.Band(76f));
        }
    }
}
