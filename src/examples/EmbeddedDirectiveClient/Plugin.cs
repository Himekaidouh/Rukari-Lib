extern alias unitycore;

using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Rukari.Lib;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;

namespace Example.Rukari.EmbeddedDirectives;

[BepInPlugin(DirectiveClient.OwnerId, "Rukari 自定义指令示例", "1.0.0")]
[BepInDependency("rukari.lib.runtime", ">=0.4.0 <0.5.0")]
public sealed class Plugin : BasePlugin
{
    private static ManualLogSource? _log;
    private static DirectiveClient? _client;
    private static Task<ModResult<DirectiveClient>>? _pending;
    private static CancellationTokenSource? _cancel;
    private static bool _attempted;
    private static long _lastLoggedNotifications;
    private static long _nextLogAt;

    public override void Load()
    {
        _log = Log;
        _cancel = new CancellationTokenSource();
        _attempted = false;
        _lastLoggedNotifications = 0;
        _nextLogAt = 0;
        AddComponent<ExampleLifetime>();
    }

    internal static void Tick()
    {
        if (_cancel is null) return;
        if (!_attempted)
        {
            // The hard dependency loads the lib first. Obtain its public service through the
            // helper and retain the successful client for the lifetime of this plugin.
            _attempted = true;
            _pending = DirectiveClient.RegisterAsync(_cancel.Token);
        }
        if (_pending is { IsCompleted: true } completed)
        {
            _pending = null;
            try
            {
                ModResult<DirectiveClient> result = completed.GetAwaiter().GetResult();
                if (result.Success)
                {
                    _client = result.Value;
                    _log?.LogInfo("已注册 #example.echo；仅统计编译通知，不执行播放效果。");
                }
                else _log?.LogWarning("自定义指令示例未注册：" + result.Error?.Message);
            }
            catch (Exception exception)
            {
                _log?.LogError("自定义指令示例注册失败：" + exception);
            }
        }

        // The compiler callback only changes managed counters. Summaries are logged here,
        // at most once per second, and contain no script text or directive payloads.
        if (_client is null || Environment.TickCount64 < _nextLogAt) return;
        CompilationStatistics statistics = _client.Snapshot();
        if (statistics.Notifications == _lastLoggedNotifications) return;
        _lastLoggedNotifications = statistics.Notifications;
        _nextLogAt = Environment.TickCount64 + 1000;
        _log?.LogInfo($"编译通知={statistics.Notifications}; 权威通知={statistics.AuthoritativeNotifications}; "
            + $"累计观察自有行={statistics.OwnedLinesObserved}; 最近权威编译={statistics.LastCompilationId}; "
            + $"最近边界={statistics.LastBoundary}; 最近自有行={statistics.LastOwnedLineCount}。这些数字不是播放次数。");
    }

    internal static void Stop()
    {
        // Unity invokes these lifecycle callbacks on the main thread. This releases our lease;
        // it does not promise hot-unloading BepInEx plugins or the shared runtime.
        CancellationTokenSource? cancel = _cancel;
        if (cancel is null) return;
        _cancel = null;
        cancel.Cancel();
        if (_pending is { IsCompletedSuccessfully: true } completed)
        {
            ModResult<DirectiveClient> result = completed.GetAwaiter().GetResult();
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
