using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Rukari.Lib;
using Rukari.Lib.Commands;
using Rukari.Lib.Spines;
using Rukari.SpineSupport.Runtime;

namespace Rukari.SpineSupport;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("rukari.lib.runtime", ">=0.4.0 <0.5.0")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "rukari.spinesupport";
    public const string Name = "更多Spine动画支持";
    public const string Version = "1.6.2";
    internal static ManualLogSource Logger { get; private set; } = null!;
    private static SpineOverlayCommandService? _overlayCommands;
    private static IDisposable? _overlayCommandLease;
    private static IDisposable? _overlayHelpLease;

    public override void Load()
    {
        Logger = Log;

        // Both switches exist because these two decisions change playback: switching one off restores the
        // official behaviour for it without touching anything else.
        bool advanceAfterEntrance = Config.Bind("Playback", "AdvanceAfterEntrance", true,
            "When the line has just played the lobby entrance animation Start_Idle_01, advance to the next line "
            + "once it has played out instead of staying there. A line that still has a voice is left to the "
            + "official voice step. Restart required.").Value;
        bool followTouchMarkers = Config.Bind("Playback", "FollowTouchMarkers", true,
            "Move the official #touch rectangle onto the lobby's own Touch_Point / Touch_Eye marker, keeping the "
            + "width and height written in the line. Restart required.").Value;
        float voiceStallSeconds = Config.Bind("Playback", "VoiceStallTimeoutSeconds", 10f,
            "Legacy compatibility key, no longer used to force dialogue advancement. Voice, post-playback delay "
            + "and AUTO wait remain owned by the voice/player system.").Value;

        // The watcher is created first: the support patch reports entrances to it as soon as it is installed.
        LobbyPlaybackWatcher? watcher = AddComponent<LobbyPlaybackWatcher>();
        watcher.Configure(advanceAfterEntrance, followTouchMarkers, voiceStallSeconds);
        if (SpineLobbySupportPatch.Install(Config))
        {
            Log.LogInfo($"更多Spine动画支持 {Version} preview loaded: animation selection, M/A pairs, one-shot entrance and "
                + $"reactions, thumbnail fitting; entranceAdvance={advanceAfterEntrance}; "
                + $"touchMarkers={followTouchMarkers}; forcedVoiceAdvance=False; "
                + $"expandAnimationList={SpineLobbySupportPatch.AnimationListExpanded}.");
        }

#if EXPERIMENTAL_SPINE_CONSOLE
        InstallOverlayConsole();
#endif
        InstallOverlayCommandService();
    }

    /// <summary>
    /// The overlay command provider (2026-09-21): the executing half of <c>#aavt;spine</c>. The shared
    /// command pipeline parses and projects the directive; this service is the only thing that writes
    /// a reserved track, and it writes nothing until a saved line asks it to.
    /// </summary>
    private void InstallOverlayCommandService()
    {
        if (!Config.Bind("OverlayCommand", "Enabled", true,
                "Register the spine overlay provider, so a saved line can keep one animation of a character's "
                + "own skeleton on a reserved track (20..29) until it is cleared or the scene rebuilds. The "
                + "engine's own tracks 0/2/3/4 are never written. Restart required.").Value)
        {
            return;
        }

        IModRuntime runtime = ModServices.Current ?? throw new InvalidOperationException("Rukari lib 尚未就绪。");
        bool holdLastFrame = Config.Bind("OverlayCommand", "HoldLastFrame", false,
            "What a non-looping overlay (#aavt;spine;…;loop=false) does when the directive does not say: "
            + "true keeps its last frame until the track is cleared, false leaves spine's own behaviour "
            + "alone. Per directive, ';hold=true|false' overrides this. Restart required.").Value;
        var service = new SpineOverlayCommandService(() => runtime.IsMainThread, holdLastFrame);
        var registered = runtime.RegisterService<ISpineOverlayCommandService>(Guid, service,
            new(SpineOverlayCapabilities.Dispatch, Guid, "0.1.0", CapabilityLevel.Experimental,
                "Writes the reserved overlay tracks only; mix is set per track entry, never on the shared "
                + "AnimationStateData; an animation the skeleton does not own is refused instead of clearing the "
                + "track; release is explicit per scene. Native lifetime validation required per game build."));
        if (!registered.Success)
        {
            service.Dispose();
            Logger.LogWarning(
                "[spine-overlay] command service was not registered: " + registered.Error?.Message);
            return;
        }

        _overlayCommands = service;
        _overlayCommandLease = registered.Value;
        Logger.LogInfo(
            "[spine-overlay] command service registered: #aavt;spine;<slot>;<animation|clear>; "
            + $"tracks {SpineOverlayTracks.First}..{SpineOverlayTracks.Last}; "
            + $"mix is per track entry; holdLastFrameByDefault={holdLastFrame}; writes=none-until-asked.");

        var help = runtime.GetService<ICommandHelpService>();
        if (help.Success)
        {
            var published = help.Value.Register(
                "叠加",
                "#aavt;spine;<槽位>;<动画名>[;track=20-29][;mix=][;fade=][;loop=][;blend=][;hold=]",
                "把该角色自己骨骼上的一条动画叠到保留轨道上，保持到写 clear 或换场景；同一张卡可以按轨道分别控制"
                + "眼睛、手臂等（轨道互不相同即可）。loop=false 的一次性动作可用 hold=true 停在尾帧。");
            if (published.Success) _overlayHelpLease = published.Value;
            else Logger.LogWarning("[spine-overlay] 指令帮助登记失败：" + published.Error!.Message);
        }
    }

    /// <summary>
    /// The overlay console (2026-09-21): an authoring surface that lists what the skeleton on stage
    /// can layer onto its idle loop and lets one be tried on a reserved track. Diagnostic/authoring
    /// only — it never writes a project, an archive or a config, and the trial always ends by itself.
    /// </summary>
#if EXPERIMENTAL_SPINE_CONSOLE
    private void InstallOverlayConsole()
    {
        if (!Config.Bind("OverlayConsole", "Enabled", true,
                "Register the Spine overlay console page in the shared toolbox: it lists the animations of "
                + "the skeleton on stage, with what each one keys and what it clashes with, and plays a "
                + "chosen one on reserved track 20 for a few seconds so it can be judged in the editor. "
                + "Nothing is saved. Restart required.").Value)
        {
            return;
        }

        float trialSeconds = Config.Bind("OverlayConsole", "TrialSeconds", 3f,
            new BepInEx.Configuration.ConfigDescription(
                "How long a trial overlay stays on before it fades out by itself.",
                new BepInEx.Configuration.AcceptableValueRange<float>(0.5f, 30f))).Value;
        float mixSeconds = Config.Bind("OverlayConsole", "MixSeconds", 0.2f,
            new BepInEx.Configuration.ConfigDescription(
                "Fade-in of a trial overlay. The engine leaves spine's default mix at zero, so this is set "
                + "per track entry and never on the shared AnimationStateData.",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 2f))).Value;
        float fadeSeconds = Config.Bind("OverlayConsole", "FadeSeconds", 0.2f,
            new BepInEx.Configuration.ConfigDescription(
                "Fade-out of a trial overlay before its reserved track is cleared.",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 2f))).Value;

        AddComponent<SpineOverlayTrialBehaviour>();
        SpineOverlayToolPage.Install(trialSeconds, mixSeconds, fadeSeconds);
    }
#endif
}
