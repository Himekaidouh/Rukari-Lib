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
    public const string Version = "0.4.4";
    private static ModRuntimeHost? _host;

    public override void Load()
    {
        var host = new ModRuntimeHost();
        try
        {
            host.Start();
            ModServices.Attach(host);
            _host = host;
            // Android/PE bisect switches (docs/50). Every gate defaults to false, so a normal install is unchanged;
            // they exist so one device session can tell which native boundary is reached before the crash.
            bool skipCompilePatch = Config.Bind("AndroidDiagnostics", "SkipCompilePatch", false,
                "诊断开关：打开后不安装共享编译补丁（Script.get_ScriptKr / CompileScriptStandalone / CompileScriptContinuous）。默认关闭。Restart required.").Value;
            bool skipEditorHost = Config.Bind("AndroidDiagnostics", "SkipEditorHost", false,
                "诊断开关：打开后不初始化共享编辑器事务与工作区服务。默认关闭。Restart required.").Value;
            bool skipInputGuards = Config.Bind("AndroidDiagnostics", "SkipInputGuards", false,
                "诊断开关：打开后不安装共享输入守卫（UICamera / UIInput 的 Harmony 前缀补丁）；工具箱会随之整体停用。默认关闭。Restart required.").Value;
            // Ownership of the keyboard and of the engine's input-method switch is process-wide, so a user whose
            // Chinese/Japanese input broke must be able to hand both back to the game without losing the tools.
            bool takeOverKeyboardAndIme = Config.Bind("Input", "TakeOverKeyboardAndIme", true,
                "是否由本模组接管键盘与输入法：开启时工具界面的输入框会暂停官方 NGUI 键盘处理，并把引擎的输入法合成开关"
                + "（Input.imeCompositionMode）临时切到 On、候选窗跟到工具的光标位置；关闭后这两件事都还给游戏，"
                + "鼠标守卫、工具箱与所有工具页照常工作，代价是工具界面内的中文/日文输入法合成不再由本模组处理。"
                + "若安装后游戏自己的输入框中文打不进去（英文正常），把它设为 false 并重启。Restart required.").Value;
            if (skipCompilePatch) Log.LogWarning("android-diagnostics: shared compile patch skipped.");
            else EmbeddedDirectiveHost.Initialize(host, message => Log.LogInfo(message));
            if (skipEditorHost) Log.LogWarning("android-diagnostics: shared editor host skipped.");
            else
            {
                try { EditorHost.Initialize(host); }
                catch (Exception exception)
                {
                    Log.LogError("Shared editor transactions unavailable; other lib services remain active: " + exception);
                }
            }
            ToolsHost.Initialize(host, message => Log.LogInfo(message), skipInputGuards, takeOverKeyboardAndIme);
            Log.LogWarning($"android-diagnostics: SkipCompilePatch={skipCompilePatch}; SkipEditorHost={skipEditorHost}; SkipInputGuards={skipInputGuards}");
            Log.LogWarning($"input: TakeOverKeyboardAndIme={takeOverKeyboardAndIme}");
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
