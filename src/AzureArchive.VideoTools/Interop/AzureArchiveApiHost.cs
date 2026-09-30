using System;
using System.Linq;
using System.Reflection;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using BepInEx.Configuration;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using AzureArchive.VideoTools.Runtime;

namespace AzureArchive.VideoTools.Interop;

internal sealed class AzureArchiveApiHost : IAzureArchiveApi
{
    private AzureArchiveApiHost(ConfigFile config)
    {
        CapabilitiesInternal = new RuntimeCapabilityService();
        SceneIdentityInternal = new ManagedSceneIdentityService(CapabilitiesInternal);
        SceneResolutionInternal = new ConfiguredSceneResolutionService(
            config,
            CapabilitiesInternal);
        FilesInternal = new ScenarioFileService(CapabilitiesInternal);
        EditorInternal = new EditorSceneService(CapabilitiesInternal, FilesInternal);
        EditorCommandDocumentsInternal = new EditorCommandDocumentService(
            CapabilitiesInternal,
            EditorInternal);
        ContinuousCompilerInternal = new ContinuousCompilerService(CapabilitiesInternal, EditorInternal);
        PlaybackInternal = new ScenarioPlaybackService(CapabilitiesInternal);
        BindStaticCapabilities();
        PlayerContextInternal = new PlayerRuntimeContextService(CapabilitiesInternal);
        ScreenTextsInternal = new ScreenTextRuntimeService(CapabilitiesInternal);
        CharacterTransformsInternal = new CharacterTransformService(CapabilitiesInternal);
        CharacterPresetsInternal = new CharacterPresetRuntime(CapabilitiesInternal);
        SceneCameraInternal = new SceneCameraService(CapabilitiesInternal);
        CharacterCommandsInternal = new CharacterTransformCommandDispatcher(
            CapabilitiesInternal,
            CharacterTransformsInternal);
        CharacterActionsInternal = new CharacterActionService(CapabilitiesInternal);
    }

    public ICapabilityService Capabilities => CapabilitiesInternal;
    public IEditorSceneService Editor => EditorInternal;
    public IEditorCommandDocumentService EditorCommandDocuments =>
        EditorCommandDocumentsInternal;
    public IEditorSceneIdentityService SceneIdentity => SceneIdentityInternal;
    public IConfiguredSceneResolutionService SceneResolution => SceneResolutionInternal;
    public IScenarioFileService Files => FilesInternal;
    public IContinuousCompilerService ContinuousCompiler => ContinuousCompilerInternal;
    public IScenarioPlaybackService Playback => PlaybackInternal;
    public IPlayerRuntimeContextService PlayerContext => PlayerContextInternal;
    public IScreenTextRuntimeService ScreenTexts => ScreenTextsInternal;
    public ICharacterTransformService CharacterTransforms => CharacterTransformsInternal;
    public ICharacterTransformCommandDispatcher CharacterCommands => CharacterCommandsInternal;
    public ISceneCameraService SceneCamera => SceneCameraInternal;
    public ICharacterActionService CharacterActions => CharacterActionsInternal;

    internal RuntimeCapabilityService CapabilitiesInternal { get; }
    internal ManagedSceneIdentityService SceneIdentityInternal { get; }
    internal ConfiguredSceneResolutionService SceneResolutionInternal { get; }
    internal ScenarioFileService FilesInternal { get; }
    internal EditorSceneService EditorInternal { get; }
    internal EditorCommandDocumentService EditorCommandDocumentsInternal { get; }
    internal ContinuousCompilerService ContinuousCompilerInternal { get; }
    internal ScenarioPlaybackService PlaybackInternal { get; }
    internal PlayerRuntimeContextService PlayerContextInternal { get; }
    internal ScreenTextRuntimeService ScreenTextsInternal { get; }
    internal CharacterTransformService CharacterTransformsInternal { get; }
    internal CharacterPresetRuntime CharacterPresetsInternal { get; }
    internal CharacterTransformCommandDispatcher CharacterCommandsInternal { get; }
    internal SceneCameraService SceneCameraInternal { get; }
    // Sibling contract (not IAzureArchiveApi): the editor preview runtime seeds
    // a scene's inherited camera start through this, so the frozen public
    // surface stays untouched.
    internal ISceneCameraCommandDispatcher SceneCameraCommandsInternal => SceneCameraInternal;
    internal CharacterActionService CharacterActionsInternal { get; }

    public static AzureArchiveApiHost Create(ConfigFile config) => new(config);

    private void BindStaticCapabilities()
    {
        BindProperty("Editor.InspectorSingleton", typeof(ScriptNodeInspector), "instance");
        BindProperty("Editor.InspectorScriptNode", typeof(ScriptNodeInspector), "scriptNode");
        BindMethod("Editor.SelectionEvent", typeof(ScriptNodeInspector), "OnChildSelect", typeof(Selectable));
        BindMethod("Editor.DataListEvent", typeof(ScriptNodeInspector), "DataList", typeof(int));
        BindProperty("Editor.Inspector", typeof(ScriptNodeInspector), "selectedScriptItem", "scriptNode");
        BindProperty("Editor.Selection", typeof(ScriptListItem), "index", "scriptNode");
        BindProperty("Editor.ScriptProperties", typeof(Script), "text", "prev", "continuous");
        BindProperty("Editor.AdditionalPromptRead", typeof(Script), "additionalPrompt");
        BindProperty(
            "Editor.AdditionalPromptInputRead",
            typeof(ScriptNodeInspector),
            "additionalPromptInput");
        BindMethod("Editor.AdditionalPromptWrite", typeof(UIInput), "Set", typeof(string), typeof(bool));
        BindMethod("Editor.AdditionalPromptCommit", typeof(ScriptNodeInspector), "SetAdditionalPrompt");
        BindProperty("Editor.ScriptList", typeof(ScriptNode), "scripts");
        BindProperty("Editor.NodeIdentity", typeof(Node), "guid");
        // Live project key surfaces (FIX B source 2, stage-gated off by
        // default). Binding at boot makes a missing interop member fail
        // loudly here instead of silently inside the runtime probe.
        BindProperty("Files.StudioInstance", typeof(StudioCommon), "instance");
        BindProperty("Files.StudioProjectName", typeof(StudioCommon), "projectName");
        BindProperty("Files.StudioSavedProjectName", typeof(StudioCommon), "savedProjectName");
        BindProperty("Files.StudioEntryNode", typeof(StudioCommon), "entryNode");
        BindProperty("Files.StudioEntryNodeGuid", typeof(Node), "guid");
        BindMethod("Compiler.ScriptCopy", typeof(Script), ".ctor", typeof(Script), typeof(bool));
        BindMethod("Compiler.MatchWithPrev", typeof(Script), "MatchWithPrev", typeof(Script));
        BindMethod("Compiler.Continuous", typeof(Script), "CompileScriptContinuous");
        BindMethod("Files.ProjectReference", typeof(ScenarioResourceManager), "LoadProject", typeof(string));
        BindMethod("Files.SaveReference", typeof(ScenarioResourceManager), "LoadGenericScenario", typeof(string));
        BindProperty("Files.OpenReference", typeof(CatalogFileItem), "infoBlock");
        BindProperty("Player.Instance", typeof(Test), "Instance");
        BindProperty("Player.PreviewMode", typeof(Test), "previewMode");
        BindProperty("Player.ScreenTexts", typeof(Test), "currentSTs");
        BindProperty(
            "Player.ScreenTextFields",
            typeof(ScenarioAnimation.ScreenTextAnimation),
            "position",
            "fontSize",
            "isMiddle",
            "targetTxt",
            "fadeInType");
        BindProperty("Player.Background", typeof(Test), "background");
        BindProperty("Player.CharacterSlots", typeof(Test), "slots");
        BindProperty("Player.CharacterPosTweener", typeof(Character), "posTweener");
        BindMethod("Player.CharacterGetPos", typeof(Character), "GetPos");
        BindMethod("Player.CharacterSetPos", typeof(Character), "SetPos");
        BindMethod(
            "Player.CharacterEnqueueAction",
            typeof(Character),
            "EnqueueAction",
            typeof(global::CharacterAction));
        BindMethod("Player.TweenPositionBegin", typeof(TweenPosition), "Begin");
        BindMethod("Player.TweenRotationBegin", typeof(TweenRotation), "Begin");
        BindMethod("Player.TweenScaleBegin", typeof(TweenScale), "Begin");
        CapabilitiesInternal.Unavailable("Player.Advance", "editor foundation: player binding deferred");
    }

    private void BindProperty(string id, Type type, params string[] names)
    {
        string[] missing = names
            .Where(name => type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static) == null)
            .ToArray();

        if (missing.Length == 0)
        {
            CapabilitiesInternal.Bound(id, $"{type.FullName}: {string.Join(", ", names)}");
        }
        else
        {
            CapabilitiesInternal.Unavailable(id, $"{type.FullName}: missing {string.Join(", ", missing)}");
        }
    }

    private void BindMethod(string id, Type type, string name, params Type[]? parameters)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        bool found;
        if (name == ".ctor")
        {
            found = type.GetConstructor(flags, null, parameters ?? Type.EmptyTypes, null) != null;
        }
        else if (parameters == null || parameters.Length == 0)
        {
            found = type.GetMethods(flags).Any(method => string.Equals(method.Name, name, StringComparison.Ordinal));
        }
        else
        {
            found = type.GetMethod(name, flags, null, parameters, null) != null;
        }

        if (found)
        {
            CapabilitiesInternal.Bound(id, $"{type.FullName}.{name}");
        }
        else
        {
            CapabilitiesInternal.Unavailable(id, $"missing {type.FullName}.{name}");
        }
    }
}
