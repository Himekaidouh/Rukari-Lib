using System;
using System.IO;
using BepInEx.Configuration;

namespace AzureArchive.VideoTools.Runtime;

internal enum PlayerCommandObservationStage
{
    Disabled = 0,
    PrefixOnly = 1,
    WindowCapture = 2,
    IdentityOnly = 3,
    WouldDispatch = 4,
    QueueNoOp = 5,
    DispatchCanary = 6,
    DispatchReplayCanary = 7,
    PlaybackContextProbe = 8,
    SceneCommandDispatch = 9
}

internal sealed record PlayerCommandObservationOptions(
    PlayerCommandObservationStage Stage,
    string ProjectPath,
    string PlaybackPath,
    string WorkspaceRoot,
    bool SceneCommandDispatchEnabled,
    bool DispatchCanaryEnabled,
    int DispatchCanaryRecordIndex,
    string DispatchCanaryCommandId,
    string DispatchCanaryDirective,
    int DispatchCanaryMaximumExecutions,
    bool EmbeddedEditorCommandsEnabled,
    bool AcceptLegacyCharacterDirectiveAlias,
    bool PreviewChainEnabled,
    bool LiveEditorChainEnabled,
    bool SlotPendingEnabled,
    bool DialogueFlowProbeEnabled,
    bool DialogueFlowProbeContentEnabled,
    bool ContinueDialogueEnabled,
    bool PreviewCameraChainEnabled)
{
    public static PlayerCommandObservationOptions Bind(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ConfigEntry<PlayerCommandObservationStage> stage = config.Bind(
            "PlayerCommandObservation",
            "Stage",
            PlayerCommandObservationStage.SceneCommandDispatch,
            "Player integration stage. PlaybackContextProbe is read-only; dispatch canary stages are separately guarded real execution stages.");
        ConfigEntry<string> workspaceRoot = config.Bind(
            "PlayerCommandObservation",
            "WorkspaceRoot",
            Path.Combine(
                BepInEx.Paths.ConfigPath,
                "AzureArchive.VideoTools",
                "workspaces"),
            "Mod-owned workspace root. Relative paths are resolved from the AzureArchive game directory.");
        ConfigEntry<bool> sceneCommandDispatchEnabled = config.Bind(
            "PlayerCommandObservation",
            "SceneCommandDispatchEnabled",
            true,
            "Explicit second switch for validated once-per-scene-window Mod command execution.");
        ConfigEntry<bool> dispatchCanaryEnabled = config.Bind(
            "PlayerCommandObservation",
            "DispatchCanaryEnabled",
            false,
            "Explicit second switch for the one-shot real playback dispatch canary.");
        ConfigEntry<int> dispatchCanaryRecordIndex = config.Bind(
            "PlayerCommandObservation",
            "DispatchCanaryRecordIndex",
            -1,
            "Exact playback record allowed by the one-shot dispatch canary.");
        ConfigEntry<string> dispatchCanaryCommandId = config.Bind(
            "PlayerCommandObservation",
            "DispatchCanaryCommandId",
            string.Empty,
            "Exact canonical command GUID allowed by the one-shot dispatch canary.");
        ConfigEntry<string> dispatchCanaryDirective = config.Bind(
            "PlayerCommandObservation",
            "DispatchCanaryDirective",
            string.Empty,
            "Exact canonical directive allowed by the one-shot dispatch canary.");
        ConfigEntry<int> dispatchCanaryMaximumExecutions = config.Bind(
            "PlayerCommandObservation",
            "DispatchCanaryMaximumExecutions",
            1,
            "Exact process execution limit required by the selected dispatch canary stage.");
        ConfigEntry<bool> embeddedEditorCommandsEnabled = config.Bind(
            "EditorEmbeddedCommands",
            "Enabled",
            true,
            "Read #aavt directives from the Environment/Additional Prompt field, sanitize them before official parsing, and compile them into playback commands.");
        ConfigEntry<bool> acceptLegacyCharacterDirectiveAlias = config.Bind(
            "EditorEmbeddedCommands",
            "AcceptLegacyCharacterDirectiveAlias",
            true,
            "Accept legacy #char directives in addition to the namespaced #aavt;char syntax.");
        ConfigEntry<bool> previewChainEnabled = config.Bind(
            "PlayerCommandObservation",
            "PreviewChainEnabled",
            true,
            "Inherit the previous scene's character command end state by applying a folded start state (duration=0) before the current scene's commands. Applies in editor preview AND formal playback: every scene entry idempotently rebuilds the chain from the project graph, so entering a scene directly matches sequential playback.");
        ConfigEntry<bool> liveEditorChainEnabled = config.Bind(
            "PlayerCommandObservation",
            "LiveEditorChainEnabled",
            true,
            "C feature (true WYSIWYG): while the editor is open, preview chain inheritance resolves against the editor's live node graph (unsaved scenes and unsaved ancestor edits included). Formal playback keeps using the saved AAP graph; any live-graph failure falls back to the disk graph automatically.");
        ConfigEntry<bool> slotPendingEnabled = config.Bind(
            "PlayerCommandObservation",
            "SlotPendingEnabled",
            true,
            "character.slotPending/v1: a #charp directive on an empty physical slot is stored during its scene and applied instantly (duration=0) when a character later enters that slot. Occupied slots fail closed; entries expire after 16 unconsumed scene windows.");
        ConfigEntry<bool> dialogueFlowProbeEnabled = config.Bind(
            "PlayerCommandObservation",
            "DialogueFlowProbeEnabled",
            false,
            "Read-only observation of the player dialogue pipeline (ClearDialogText, ParseScript, typewriter). Logs lengths only, never dialogue content.");
        ConfigEntry<bool> dialogueFlowProbeContentEnabled = config.Bind(
            "PlayerCommandObservation",
            "DialogueFlowProbeContentEnabled",
            false,
            "Dialogue flow probe only: additionally log up to 24 characters of each observed string. Research opt-in; leave off for normal use.");
        ConfigEntry<bool> continueDialogueEnabled = config.Bind(
            "PlayerCommandObservation",
            "ContinueDialogueEnabled",
            true,
            "dialogue.continue/v1: a bare #aavt;continue line in the Environment Additional Prompt keeps the previous visible dialogue line and types only this scene's text after it. The marker scene must follow a scene with dialogue.");
        ConfigEntry<bool> previewCameraChainEnabled = config.Bind(
            "PlayerCommandObservation",
            "PreviewCameraChainEnabled",
            true,
            "Editor preview only: fold ancestor #aavt;camera directives into the composition this scene already shows in sequential playback, and apply that state (duration=0) before the scene's own commands. Playback inherits the live camera by itself; the editor preview rebuilds the scene per click, so without this a scene previews from the default framing. Disable to compare against the old behaviour.");
        ConfigEntry<string> projectPath = config.Bind(
            "SceneIdentityFiles",
            "ProjectPath",
            string.Empty,
            "Absolute path to the AAP project used for read-only scene identity resolution.");
        ConfigEntry<string> playbackPath = config.Bind(
            "SceneIdentityFiles",
            "PlaybackPath",
            string.Empty,
            "Absolute path to the matching AAS playback archive used for read-only resolution.");

        return new PlayerCommandObservationOptions(
            stage.Value,
            projectPath.Value ?? string.Empty,
            playbackPath.Value ?? string.Empty,
            workspaceRoot.Value ?? string.Empty,
            sceneCommandDispatchEnabled.Value,
            dispatchCanaryEnabled.Value,
            dispatchCanaryRecordIndex.Value,
            dispatchCanaryCommandId.Value ?? string.Empty,
            dispatchCanaryDirective.Value ?? string.Empty,
            dispatchCanaryMaximumExecutions.Value,
            embeddedEditorCommandsEnabled.Value,
            acceptLegacyCharacterDirectiveAlias.Value,
            previewChainEnabled.Value,
            liveEditorChainEnabled.Value,
            slotPendingEnabled.Value,
            dialogueFlowProbeEnabled.Value,
            dialogueFlowProbeContentEnabled.Value,
            continueDialogueEnabled.Value,
            previewCameraChainEnabled.Value);
    }
}
