extern alias unitycore;

using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Interop;
using AzureArchive.VideoTools.Runtime;
using KeyCode = unitycore::UnityEngine.KeyCode;

namespace AzureArchive.VideoTools;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("rukari.lib.runtime", ">=0.4.2 <0.5.0")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "rukari.moreeffects";
    public const string Name = "更多的画面效果";
    public const string Version = "1.4.1";

    internal static ManualLogSource Logger { get; private set; } = null!;
    public static IAzureArchiveApi Api { get; private set; } = null!;
    internal static AzureArchiveApiHost Host { get; private set; } = null!;
    private static IDisposable? _saveServiceLease;

    /// <summary>
    /// Publishes the on-demand save to the shared rail page (2026-09-21). Rukari lib declares the
    /// contract and draws the page; only this package can reach the editor's session, so it supplies
    /// the implementation. A failure here leaves the page saying so instead of pretending to save.
    /// </summary>
    private void InstallSaveService()
    {
        if (!Config.Bind("EditorSave", "Enabled", true,
                "Publish the on-demand save (save -> compile -> publish the playable file) that the shared "
                + "「工程保存」 rail page calls. Turn it off to leave the page reporting that no implementation "
                + "is available. Restart required.").Value)
        {
            return;
        }

        if (Rukari.Lib.ModServices.Current is not { } runtime)
        {
            Logger.LogWarning("Save service was not registered: Rukari lib runtime is not ready.");
            return;
        }

        var registered = runtime.RegisterService<Rukari.Lib.Editor.IEditorSaveService>(
            Guid,
            new EditorSaveService(),
            new(Rukari.Lib.Editor.EditorSaveCapabilities.OnDemand, Guid, "0.1.0",
                Rukari.Lib.CapabilityLevel.Experimental,
                "Runs the editor's own Save(false), Compile() and Compile(projectName) in order on demand; "
                + "each step is reported separately and nothing is written outside the editor's own paths."));
        if (!registered.Success)
        {
            Logger.LogWarning("Save service was not registered: " + registered.Error?.Message);
            return;
        }

        _saveServiceLease = registered.Value;
        Logger.LogInfo("Save service registered: the shared 工程保存 page can now run save → compile → publish.");
    }

    public override void Load()
    {
        Logger = Log;
        // The feature package is usable without a developer's legacy cfg. Existing explicit
        // values still win; only a new profile receives these read-only discovery defaults.
        Config.Bind("SceneIdentityFiles", "Enabled", true, "Resolve the selected scene from read-only project files.");
        Config.Bind("SceneIdentityFiles", "AutoDiscoverEnabled", true, "Discover the open project without entering AAP/AAS paths.");
        Config.Bind("SceneIdentityFiles", "LiveProjectKeyFromStudioCommon", true, "Resolve unsaved editor identity using the existing guarded two-probe live source.");
        Host = AzureArchiveApiHost.Create(Config);
        Api = Host;
        AutoDialogueDelayPatch.Install();
        ExplicitTriggerBehaviour.Initialize(CharacterTransformProofOptions.Bind(Config));
        AddComponent<ExplicitTriggerBehaviour>();
        VisualEditorOptions visualEditorOptions = VisualEditorOptions.Bind(Config);
        bool visualEditorReady = visualEditorOptions.Enabled
            && VisualEditorInputGuard.Install();
        if (visualEditorReady)
        {
            VisualEditorBehaviour.Initialize(visualEditorOptions);
            AddComponent<VisualEditorBehaviour>();
        }
        PlayerCommandObservationOptions observationOptions =
            PlayerCommandObservationOptions.Bind(Config);
        bool playerObservationReady =
            PlayerCommandObservationRuntime.Initialize(observationOptions);
        if (playerObservationReady)
        {
            AddComponent<PlayerCommandObservationBehaviour>();
        }
        if (observationOptions.DialogueFlowProbeEnabled && playerObservationReady)
        {
            DialogueFlowObservationProbe.Install(
                observationOptions.DialogueFlowProbeContentEnabled);
        }
        if (observationOptions.ContinueDialogueEnabled)
        {
            ContinueDialogueRuntime.Install(
                Path.Combine(
                    Paths.ConfigPath,
                    "AzureArchive.VideoTools",
                    "continue-dialogue-identities.txt"),
                observationOptions.DialogueFlowProbeContentEnabled,
                observationOptions.DialogueFlowProbeEnabled);
        }
        bool embeddedEditorCommandsReady = observationOptions.EmbeddedEditorCommandsEnabled
            && playerObservationReady
            && EditorEmbeddedDirectivePatch.Install(
                observationOptions.AcceptLegacyCharacterDirectiveAlias);
        PlayerCommandObservationRuntime.SetEmbeddedEditorCommandsRuntimeReady(
            embeddedEditorCommandsReady);
        ManagedUnityLogSelectionProbe.Install();
        // OnChildSelect(Selectable) is blacklisted; retain the DataList/log observation path.
        EditorDataListEventProbe.Install();
        MoreEffectsToolPage.Install();
        MoreEffectsCommandHelp.Install();
        InstallSaveService();
        Logger.LogWarning(
            $"{Name} {Version} scene-command replay candidate loaded; "
            + $"capabilities={Api.Capabilities.GetAll().Count}; harmony=staged-manual-hooks; "
            + "newBoundary=BepInEx.ILogListener-managed-string-only; "
            + "candidateRule=first-message-after-DataList; identity=full-sha256-length-lines; "
            + "fileBinding=explicit-config-only; resolution=managed-background-read-only; "
            + "trust=selected-scene-plus-archive-freshness-plus-legacy-pairing; "
            + "gameApiAccess=main-thread-explicit-service-only; "
            + "manualSlotSelect=Alt+1..5; manualMove=playback-only-arrows-left-right/F4-reset; "
            + "manualAction=playback-only-up-Jump/down-Greeting-candidate; "
            + "previewPolicy=directives-allowed-manual-controls-suppressed; "
            + "commandSources=manual-or-validated-playback-workspace; wrapperCache=false; "
            + "harmonyDetail=two-editor-postfixes-plus-configured-player-stage; "
            + $"playerObservationStage={observationOptions.Stage}; "
            + $"playerCommandDispatch={(observationOptions.Stage == PlayerCommandObservationStage.SceneCommandDispatch ? "validated-once-per-scene-window" : observationOptions.Stage is PlayerCommandObservationStage.DispatchCanary or PlayerCommandObservationStage.DispatchReplayCanary ? "guarded-canary" : "false")}; "
            + "sceneDispatchTiming=same-frame-late-update-after-pending-scan; "
            + "sceneReplayTransformPolicy=restore-command-axes-to-captured-baseline; "
            + "sceneCamera=Back-plus-Spine-local-transform; frontUIWritten=false; "
            + "continueIdentityPolicy=current-aap-aas-authoritative-replace; "
            + "autoDialogueDelay=official-tier-2-runtime-mapped-to-2.5s-storage-unchanged; "
            + $"visualEditor={(visualEditorReady ? "slot-anchor-aware-precision-composer-plus-fixed-slot-selector-plus-guarded-apply-and-undo" : "disabled")}; "
            + "editorCommandDocument=revision-locked-apply-readback-rollback-undo; "
            + "commandAuthoring=isolated-sidecar-cli-or-environment-additional-prompt; "
            + $"embeddedEditorCommands={embeddedEditorCommandsReady}; embeddedSyntax=#aavt-char-v1; "
            + "embeddedOfficialParserPolicy=managed-ScriptKr-plus-compiler-result-filter; editorPreviewBinding=one-shot-selection-lease-plus-live-scene-address-no-save-required; commandIndexReload=Ctrl+F6; "
            + "archiveWritePolicy=no-direct-aap-or-aas-write; editorApply=official-additional-prompt-input-path; "
            + "stable-0.7.17-arrow-controls-and-actions=retained.");
    }
}

internal static class VideoToolsConfig
{
    public static ConfigEntry<KeyCode> ToggleUiKey { get; private set; } = null!;
    public static ConfigEntry<KeyCode> ScreenshotKey { get; private set; } = null!;
    public static ConfigEntry<KeyCode> ToggleSlowMotionKey { get; private set; } = null!;
    public static ConfigEntry<KeyCode> RunFoundationProofKey { get; private set; } = null!;
    public static ConfigEntry<bool> FoundationProofEnabled { get; private set; } = null!;
    public static ConfigEntry<bool> FoundationProofVisibleMarker { get; private set; } = null!;
    public static ConfigEntry<float> SlowMotionScale { get; private set; } = null!;
    public static ConfigEntry<string> ScreenshotDirectory { get; private set; } = null!;

    public static void Bind(ConfigFile config)
    {
        ToggleUiKey = config.Bind("Hotkeys", "ToggleUiKey", KeyCode.F9, "Call the story player HideUI action.");
        ScreenshotKey = config.Bind("Hotkeys", "ScreenshotKey", KeyCode.F10, "Capture a PNG screenshot.");
        ToggleSlowMotionKey = config.Bind("Hotkeys", "ToggleSlowMotionKey", KeyCode.F11, "Toggle normal speed and slow motion.");
        RunFoundationProofKey = config.Bind("Hotkeys", "RunFoundationProofKey", KeyCode.F5, "Run the safe compatibility and official continuous compiler proof on the selected editor scene.");
        FoundationProofEnabled = config.Bind("Diagnostics", "FoundationProofEnabled", true, "Enable the compatibility API proof and its file log.");
        FoundationProofVisibleMarker = config.Bind("Diagnostics", "FoundationProofVisibleMarker", true, "Mark the selected script row after an F5 proof without changing project data.");
        SlowMotionScale = config.Bind("Video", "SlowMotionScale", 0.5f, "Time.timeScale value used by slow motion.");
        ScreenshotDirectory = config.Bind("Video", "ScreenshotDirectory", "BepInEx/screenshots", "Relative or absolute screenshot output directory.");
    }
}
