extern alias unitycore;

using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Rukari.Lib;
using Client = UiToolboxClient.UiToolboxClient;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;

namespace Example.Rukari.UiToolbox;

[BepInPlugin("example.rukari.ui", "Rukari UI 示例", "1.0.0")]
[BepInDependency("rukari.lib.runtime", ">=0.4.0 <0.5.0")]
public sealed class Plugin : BasePlugin
{
    private static ManualLogSource? _log;
    private static Client? _client;
    private static Task<ModResult<Client>>? _pending;
    private static CancellationTokenSource? _cancel;
    private static bool _attempted;

    public override void Load()
    {
        _log = Log;
        _cancel = new CancellationTokenSource();
        _attempted = false;
        AddComponent<ExampleLifetime>();
    }

    internal static void Tick()
    {
        if (_cancel is null) return;
        if (!_attempted)
        {
            // The hard dependency loads the shared host first. Registration runs through its
            // main-thread dispatcher; never Wait() or block a queued Task from this callback.
            _attempted = true;
            _pending = Client.RegisterAsync(_cancel.Token);
        }
        if (_pending is not { IsCompleted: true } completed) return;
        _pending = null;
        try
        {
            ModResult<Client> result = completed.GetAwaiter().GetResult();
            if (result.Success)
            {
                _client = result.Value;
                _log?.LogInfo("UI 示例已注册；在工程内打开 Script 节点后查看左侧扩展工具。");
            }
            else _log?.LogWarning("UI 示例未注册：" + result.Error?.Message);
        }
        catch (Exception exception)
        {
            _log?.LogError("UI 示例注册失败：" + exception);
        }
    }

    internal static void Stop()
    {
        // Unity lifecycle callbacks execute on the game main thread. Cancellation prevents a
        // queued registration from starting. An already completed registration must be disposed.
        CancellationTokenSource? cancel = _cancel;
        if (cancel is null) return;
        _cancel = null;
        cancel.Cancel();
        if (_pending is { IsCompletedSuccessfully: true } completed)
        {
            ModResult<Client> result = completed.GetAwaiter().GetResult();
            if (result.Success) result.Value.Dispose();
        }
        _pending = null;
        _client?.Dispose();
        _client = null;
        cancel.Dispose();
    }
}

public sealed class ExampleLifetime : MonoBehaviour
{
    public ExampleLifetime(IntPtr pointer) : base(pointer) { }
    public void Update() => Plugin.Tick();
    public void OnDestroy() => Plugin.Stop();
    public void OnApplicationQuit() => Plugin.Stop();
}
