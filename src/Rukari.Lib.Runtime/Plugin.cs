extern alias unitycore;

using Rukari.Lib;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Rukari.Lib.Runtime.Commands;
using Rukari.Lib.Runtime.Editor;
using Rukari.Lib.Runtime.Tools;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;

namespace Rukari.Lib.Runtime;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "rukari.lib.runtime";
    public const string Name = "Rukari lib";
    public const string Version = "0.4.1";
    private static ModRuntimeHost? _host;

    public override void Load()
    {
        var host = new ModRuntimeHost();
        try
        {
            host.Start();
            ModServices.Attach(host);
            _host = host;
            EmbeddedDirectiveHost.Initialize(host, message => Log.LogInfo(message));
            try { EditorHost.Initialize(host); }
            catch (Exception exception)
            {
                Log.LogError("Shared editor transactions unavailable; other lib services remain active: " + exception);
            }
            ToolsHost.Initialize(host, message => Log.LogInfo(message));
            OfficialUiStyle.Log(message => Log.LogInfo(message));
            bool commandHelp = Config.Bind("CommandHelp", "Enabled", false,
                "把各 mod 自己的指令追加进编辑器「额外指令」旁的「指令格式」面板。"
                + "默认关闭：该面板只认它自己编译进去的那份条目，追加进去的行会显示成没有语法、点不动的分组文字，"
                + "而且这一项要写入面板自己的条目字段；在弄清它期望的数据形状之前保持关闭，面板保持官方原样。"
                + "Restart required.").Value;
            CommandHelpHost.Initialize(host, commandHelp, message => Log.LogInfo(message));
            AddComponent<RuntimeBehaviour>();
            Log.LogInfo($"Rukari lib {Version} ready; shared editor transactions, node-scoped toolbox and main-thread dispatch.");
        }
        catch
        {
            CommandHelpHost.Shutdown();
            EmbeddedDirectiveHost.Shutdown();
            ToolsHost.Shutdown();
            EditorHost.Shutdown();
            ModServices.Detach(host);
            host.Dispose();
            if (ReferenceEquals(_host, host)) _host = null;
            throw;
        }
    }

    internal static void Pump()
    {
        _host?.Pump(32);
        ToolsHost.Tick();
    }
    internal static void Stop()
    {
        ModRuntimeHost? host = Interlocked.Exchange(ref _host, null);
        if (host == null) return;
        CommandHelpHost.Shutdown();
        EmbeddedDirectiveHost.Shutdown();
        ToolsHost.Shutdown();
        EditorHost.Shutdown();
        ModServices.Detach(host);
        host.Dispose();
    }
}

public sealed class RuntimeBehaviour : MonoBehaviour
{
    public RuntimeBehaviour(IntPtr pointer) : base(pointer) { }
    public void Update() => Plugin.Pump();
    public void OnDestroy() => Plugin.Stop();
    public void OnApplicationQuit() => Plugin.Stop();
}
