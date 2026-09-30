extern alias unitycore;

using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Rukari.CharacterVoice.Core;
using Rukari.CharacterVoice.Interop;
using Rukari.CharacterVoice.Runtime;
using Rukari.Lib;
using Rukari.Lib.Commands;
using Rukari.Lib.Editor;
using Rukari.Lib.Tools;
using Rukari.Lib.Settings;
using Rukari.Lib.Voices;
using MonoBehaviour = unitycore::UnityEngine.MonoBehaviour;

namespace Rukari.CharacterVoice;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("rukari.lib.runtime", ">=0.4.1 <0.5.0")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "rukari.charactervoice";
    public const string Name = "人物配音支持";
    public const string Version = "1.6.2";
    internal static ManualLogSource Logger { get; private set; } = null!;
    private static VoiceAuthoringSession? _voice;
    private static VoiceToolPage? _page;
    private static IDisposable? _serviceLease;
    private static IDisposable? _pageLease;
    private static IDisposable? _settingsLease;
    private static IDisposable? _helpLease;
    private static bool _probeEnabled;
    private static bool _probeCompile;
    private static string _diagnosticsRoot = string.Empty;

    public override void Load()
    {
        Logger = Log;
        _probeEnabled = Config.Bind("Diagnostics", "ProbeAuthoringCatalog", false,
            "Read-only reconnaissance of the editor's own resource catalog (AzureArchive.Automation): the session, the "
            + "catalog version, which spelling of a manifest entry the catalog knows, and the editor's tool vocabulary. "
            + "It calls no write path and runs once per project. Turn it off once the official cleanup identifier is known.")
            .Value;
        _probeCompile = Config.Bind("Diagnostics", "RunAuthoringCompile", false,
            "During the catalog probe, also ask the editor's own automation session for the project context and a "
            + "compile receipt, so a missing playable file can be diagnosed from its own answer instead of from disk. "
            + "Diagnostic only; turn it off when the question is answered.").Value;
        _diagnosticsRoot = Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath) ?? Path.GetTempPath(),
            "Rukari.CharacterVoice", "diagnostics");
        IModRuntime runtime = ModServices.Current ?? throw new InvalidOperationException("Rukari lib 尚未就绪。");
        var editor = runtime.GetService<IEditorDocumentService>();
        var toolbox = runtime.GetService<IToolboxService>();
        if (!editor.Success || !toolbox.Success)
            throw new InvalidOperationException(editor.Error?.Message ?? toolbox.Error?.Message ?? "Rukari lib 编辑服务尚未就绪。");
        VoicePlaceholderProbe.Install(Config);
        GameVoiceFileProbe.Install(Config);
        VoiceStageCopyGuard.Install(Config);
        AuthoringSaveProbe.Install(Config);
        CompileRevisionGuard.Install(Config);
        PublishProbe.Install(Config);
        ImportedVoiceCatalogService.InitializeOnMainThread();
        ProjectVoiceImportStore.InitializeOnMainThread();
        if (!VoiceSupportPatch.Install(Config))
        {
            Logger.LogWarning("人物配音支持未启用或原生语音接口安装失败；未注册创作页面。");
            return;
        }
        try
        {
            _voice = new VoiceAuthoringSession(new SharedEditorVoiceBackend(editor.Value),
                () => runtime.State == RuntimeState.Ready, () => runtime.IsMainThread);
            var registered = runtime.RegisterService<IVoiceAuthoringService>(Guid, _voice,
                new(VoiceCapabilities.Authoring, Guid, "0.1.0", CapabilityLevel.Experimental,
                    "Owns AAVT directive voice bindings and imported sound resources; native playback and shared editor transactions; native lifecycle validation required per game build."));
            if (!registered.Success) throw new InvalidOperationException(registered.Error!.Message);
            _serviceLease = registered.Value;
            var page = new VoiceToolPage(_voice, editor.Value,
                ProjectVoiceImportStore.CaptureCurrentProjectOnMainThread,
                (project, scan, cancellation) => new ProjectVoiceImportStore(project).ImportAsync(scan, cancellation),
                backupRoot: Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath) ?? Path.GetTempPath(),
                    "voice-cleanup-backups")) { ReadPublicationStatus = VoicePublicationRuntime.ReadCurrentStatus };
            _page = page;
            var registeredPage = toolbox.Value.RegisterPage(Guid, VoiceToolPage.PageId, Name, page.Snapshot, page.Handle, "wave");
            if (!registeredPage.Success) throw new InvalidOperationException(registeredPage.Error!.Message);
            _pageLease = registeredPage.Value;
            var playbackSettings = new VoicePlaybackSettingsPage(() => VoicePlaybackRuntime.PostPlaybackDelaySeconds,
                VoicePlaybackRuntime.SavePostPlaybackDelay);
            var globalSettings = runtime.GetService<IModSettingsService>();
            var settingsPage = globalSettings.Success
                ? globalSettings.Value.RegisterPage(Guid, Name, playbackSettings)
                : toolbox.Value.RegisterHostedPage(Guid, VoicePlaybackSettingsPage.PageId, "播放设置", playbackSettings, "wave");
            if (!globalSettings.Success)
                Logger.LogWarning("全局模组设置暂不可用；播放设置仍可从人物配音工具页打开，配置位置不变。");
            if (!settingsPage.Success) throw new InvalidOperationException(settingsPage.Error!.Message);
            _settingsLease = settingsPage.Value;
            var help = runtime.GetService<ICommandHelpService>();
            if (help.Success)
            {
                // Optional: the lib only publishes when its own switch is on, so a missing service is not a fault.
                var published = help.Value.Register("语音", "#aavt;voice;<资源键>",
                    "让这一句播放工程里导入的语音；在「人物配音支持」工具页选好声音后会自动写入这一行。");
                if (published.Success) _helpLease = published.Value;
                else Logger.LogWarning("指令帮助登记失败：" + published.Error!.Message);
            }
            AddComponent<VoiceBehaviour>();
            Logger.LogInfo($"人物配音支持 {Version} ready; project voice folder import, independent voice playback, global post-voice delay (0-10s), and "
                + "portable published voice companion; native STA folder picker; dependency=Rukari lib 0.4.1; "
                + "legacy #aavt voice identifiers retained.");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal static void Stop()
    {
        Interlocked.Exchange(ref _settingsLease, null)?.Dispose();
        Interlocked.Exchange(ref _pageLease, null)?.Dispose();
        Interlocked.Exchange(ref _helpLease, null)?.Dispose();
        Interlocked.Exchange(ref _page, null)?.Dispose();
        Interlocked.Exchange(ref _serviceLease, null)?.Dispose();
        Interlocked.Exchange(ref _voice, null)?.Dispose();
        VoiceSupportPatch.Stop();
    }

    internal static void TickPage() => _page?.Tick();

    /// <summary>
    /// The catalog probe waits for a project to open, so it runs from the same per-frame behaviour that already
    /// owns the main-thread voice work. It is read-only and stops after the first project it sees.
    /// </summary>
    internal static void TickProbe() => AuthoringCatalogProbe.TickOnMainThread(_probeEnabled, _diagnosticsRoot, _probeCompile);
}

public sealed class VoiceBehaviour : MonoBehaviour
{
    public VoiceBehaviour(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        VoicePlaybackRuntime.TickOnMainThread();
        Plugin.TickPage();
        Plugin.TickProbe();
    }
    public void OnDestroy() => Plugin.Stop();
    public void OnApplicationQuit() => Plugin.Stop();
}
