using System;
using System.Collections.Generic;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Api;

public enum CapabilityStatus
{
    Unavailable = 0,
    Bound = 1,
    RuntimeVerified = 2,
    Degraded = 3,
    Blacklisted = 4
}

public sealed record CapabilitySnapshot(string Id, CapabilityStatus Status, string Detail);

public sealed class ApiResult<T>
{
    private ApiResult(bool success, T? value, string error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    public bool Success { get; }
    public T? Value { get; }
    public string Error { get; }

    public static ApiResult<T> Ok(T value) => new(true, value, string.Empty);
    public static ApiResult<T> Fail(string error) => new(false, default, error);
}

public sealed record SceneAddress(
    string ProjectKey,
    string NodeGuid,
    int SceneIndex,
    string Fingerprint);

public sealed record SceneSnapshot(
    SceneAddress Address,
    string DialogueText,
    string PreviousDialogueText,
    bool HasPreviousScene);

public sealed record InspectorReferenceProbe(
    bool InspectorReferenceReturned,
    bool ScriptNodeReferenceReturned,
    string Source);

public sealed record EditorSceneIdentitySnapshot(
    long ObservationSequence,
    int SelectionRequestId,
    string CompiledScriptSha256,
    int CompiledScriptLength,
    int CompiledScriptLineCount);

public enum ConfiguredSceneResolutionStatus
{
    Disabled = 0,
    NotConfigured = 1,
    Failed = 2,
    NotFound = 3,
    AmbiguousPlaybackScript = 4,
    PlaybackRecordOnly = 5,
    Mapped = 6
}

public sealed record ConfiguredSceneResolutionSnapshot(
    long ObservationSequence,
    ConfiguredSceneResolutionStatus Status,
    string ProjectPath,
    string PlaybackPath,
    string ProjectRevisionSha256,
    string PlaybackRevisionSha256,
    int? PlaybackRecordIndex,
    string NodeGuid,
    int? SceneIndex,
    string SceneFingerprint,
    bool SelectedSceneTrusted,
    bool ArchivePairFresh,
    bool PairingTrusted,
    string Diagnostic);

public sealed record ScenarioFileSnapshot(
    string ProjectReference,
    string SaveReference,
    string ProjectKey,
    string LastSource);

public sealed record ContinuousCompileProof(
    SceneAddress Address,
    bool CompatibleWithPrevious,
    bool MatchSetPrevious,
    bool MatchSetContinuous,
    string PreviousText,
    string CurrentText,
    string StandaloneOutput,
    string ContinuousOutput);

public sealed record PlaybackSnapshot(int RowIndex, bool HasDialog, string VisibleText);

public sealed record CharacterTransformExecutionSnapshot(
    string SceneIdentity,
    string OccupantIdentifier,
    CharacterTransformCommand Command,
    CharacterTransformState Before,
    CharacterTransformState Target,
    bool PositionChanged,
    bool RotationChanged,
    bool BaselineCaptured,
    bool IsReset);

public sealed record SceneCameraExecutionSnapshot(
    string SceneIdentity,
    SceneCameraCommand Command,
    SceneCameraState Before,
    SceneCameraState Target,
    bool PositionChanged,
    bool ZoomChanged,
    bool BaselineCaptured,
    bool ReplayRestored,
    bool IsReset);

public sealed record SceneCameraReadSnapshot(
    SceneCameraState State,
    int BackInstanceId,
    int SpineInstanceId,
    bool PhysicalBaselinesCaptured);

public sealed record CharacterSlotSnapshot(
    int PublicSlot,
    bool Occupied,
    string OccupantIdentifier,
    CharacterTransformState? State);

public sealed record EditorCommandDocumentSnapshot(
    SceneAddress Address,
    string DialogueText,
    string AdditionalPrompt,
    string RevisionSha256,
    int OfficialLineCount,
    int AavtLineCount,
    bool HasContinueDirective,
    bool HasClearScreenTextDirective,
    bool InputAvailable,
    bool InputMatchesScript,
    bool UndoAvailable,
    IReadOnlyList<string> CanonicalDirectives,
    IReadOnlyList<ScreenTextLine> ScreenTextLines,
    IReadOnlyList<int> OfficialPositionTransitionSlots)
{
    // Shared editor selection token captured when this snapshot was displayed. Never reconstruct
    // it from a pointer, address or content hash: returning to the same line must invalidate drafts.
    internal string RuntimeSelectionKey { get; init; } = string.Empty;
}

public sealed record EditorCommandEditPreviewSnapshot(
    EditorCommandDocumentSnapshot Source,
    string ResultRevisionSha256,
    string UpdatedAdditionalPrompt,
    string CanonicalPublicDirective,
    int PublicSlot,
    bool ReplacedExisting);

public sealed record EditorCommandApplySnapshot(
    EditorCommandDocumentSnapshot Before,
    EditorCommandDocumentSnapshot After,
    string CanonicalPublicDirective,
    int PublicSlot,
    bool ReplacedExisting);

public sealed record EditorCommandContinueApplySnapshot(
    EditorCommandDocumentSnapshot Before,
    EditorCommandDocumentSnapshot After,
    bool ContinueEnabled,
    bool AlreadyPresent);

public sealed record EditorCommandVoiceApplySnapshot(
    EditorCommandDocumentSnapshot Before,
    EditorCommandDocumentSnapshot After,
    string? ResourceKey,
    bool AlreadyPresent);

public sealed record EditorCommandScreenTextApplySnapshot(
    EditorCommandDocumentSnapshot Before,
    EditorCommandDocumentSnapshot After,
    ScreenTextDirective? Directive,
    bool ReplacedExisting,
    bool AlreadyPresent);

public sealed record EditorCommandClearScreenTextApplySnapshot(
    EditorCommandDocumentSnapshot Before,
    EditorCommandDocumentSnapshot After,
    bool AlreadyPresent);

public sealed record VisibleScreenTextSnapshot(
    ScreenTextDirective Directive,
    string Text);

public sealed record EditorCommandUndoSnapshot(
    EditorCommandDocumentSnapshot BeforeUndo,
    EditorCommandDocumentSnapshot Restored);

public enum CharacterCommandSource
{
    DirectApi = 0,
    ManualControl = 1,
    PlaybackSidecar = 2
}

public sealed record CharacterTransformDispatchRequest(
    string SceneIdentity,
    string Directive,
    CharacterCommandSource Source,
    int? PublicSlotOverride = null,
    string OriginIdentity = "");

public sealed record CharacterTransformDispatchSnapshot(
    long Sequence,
    CharacterCommandSource Source,
    CharacterTransformExecutionSnapshot Execution);

public sealed record CharacterTransformInheritedStartSnapshot(
    string SceneIdentity,
    int PublicSlot,
    string OccupantIdentifier,
    CharacterTransformState Before,
    CharacterTransformState Applied,
    bool PositionWritten,
    bool RotationWritten,
    bool BaselineCaptured);

public enum CharacterActionKind
{
    Greeting = 1,
    FalldownLeft = 2,
    FalldownRight = 3,
    Stiff = 4,
    Shake = 5,
    Jump = 6,
    Hophop = 7
}

public sealed record CharacterActionRequest(
    int PublicSlot,
    CharacterActionKind Action,
    CharacterCommandSource Source);

public sealed record CharacterActionExecutionSnapshot(
    long Sequence,
    int PublicSlot,
    string OccupantIdentifier,
    CharacterActionKind Action,
    CharacterCommandSource Source);

public interface ICapabilityService
{
    CapabilitySnapshot Get(string id);
    IReadOnlyList<CapabilitySnapshot> GetAll();
}

public interface IEditorSceneService
{
    ApiResult<InspectorReferenceProbe> ProbeInspectorReference();
    ApiResult<SceneSnapshot> GetSelectedScene();
}

public interface IEditorSceneIdentityService
{
    EditorSceneIdentitySnapshot? Current { get; }
}

internal static class RuntimeMutationFailure
{
    public const string ResidualMutationMarker = "residualMutationPossible=true";
}

public interface IEditorCommandDocumentService
{
    ApiResult<EditorCommandDocumentSnapshot> ReadSelectedOnMainThread();

    ApiResult<EditorCommandEditPreviewSnapshot> PreviewUpsertSelectedOnMainThread(
        string expectedRevisionSha256,
        string directive,
        string? expectedSelectionToken = null);

    ApiResult<EditorCommandApplySnapshot> ApplyUpsertSelectedOnMainThread(
        string expectedRevisionSha256,
        string directive,
        string? expectedSelectionToken = null);

    ApiResult<EditorCommandContinueApplySnapshot> ApplyContinueToggleOnMainThread(
        bool enabled,
        string? expectedSelectionToken = null);

    ApiResult<EditorCommandScreenTextApplySnapshot> ApplyScreenTextOnMainThread(
        string expectedRevisionSha256,
        ScreenTextDirective? directive,
        string? expectedSelectionToken = null);

    ApiResult<EditorCommandClearScreenTextApplySnapshot>
        ApplyClearScreenTextOnMainThread(string expectedRevisionSha256, string? expectedSelectionToken = null);

    ApiResult<EditorCommandUndoSnapshot> UndoSelectedOnMainThread(string? expectedSelectionToken = null);
}

public interface IConfiguredSceneResolutionService
{
    ConfiguredSceneResolutionSnapshot? Current { get; }
}

public interface IScenarioFileService
{
    ScenarioFileSnapshot Current { get; }
}

public interface IContinuousCompilerService
{
    ApiResult<ContinuousCompileProof> CompileSelectedCopy();
}

public interface IScenarioPlaybackService
{
    PlaybackSnapshot? Current { get; }
    event Action<PlaybackSnapshot>? ScenarioAdvanced;
}

public interface IPlayerRuntimeContextService
{
    ApiResult<PlayerRuntimeContextSnapshot> ReadOnMainThread();
}

public interface IScreenTextRuntimeService
{
    ApiResult<IReadOnlyList<VisibleScreenTextSnapshot>> ReadVisibleOnMainThread();
}

public interface ICharacterTransformService
{
    ApiResult<IReadOnlyList<CharacterSlotSnapshot>> ReadSlotsOnMainThread();

    ApiResult<CharacterTransformExecutionSnapshot> ExecuteOnMainThread(
        string sceneIdentity,
        string directive);

    ApiResult<CharacterTransformExecutionSnapshot> ExecuteCommandOnMainThread(
        string sceneIdentity,
        CharacterTransformCommand command);

    ApiResult<bool> ClearRuntimeBaselines();
}

public interface ISceneCameraService
{
    ApiResult<SceneCameraReadSnapshot> ReadOnMainThread();

    ApiResult<SceneCameraExecutionSnapshot> ExecuteOnMainThread(
        string sceneIdentity,
        string canonicalDirective);
}

/// <summary>
/// Sibling contract of <see cref="ISceneCameraService"/> (2026-09-18) for scene
/// camera inheritance in the editor preview. The frozen camera contract cannot
/// gain a member, and the operation cannot be expressed as a synthetic
/// <c>set</c> dispatch: the next relative command in the same scene
/// replay-restores to the scene entry first, which would wipe a synthetic
/// start. The implementation therefore seeds the entry itself.
/// </summary>
public interface ISceneCameraCommandDispatcher
{
    /// <summary>
    /// Seeds one scene's entry state with the state that scene inherits from its
    /// ancestors in sequential playback and applies it instantly
    /// (<c>duration=0</c>). Commands of the same scene afterwards plan from the
    /// inherited state, and green replays restore to it instead of to the
    /// default composition.
    /// </summary>
    ApiResult<SceneCameraInheritedStartSnapshot> ApplyInheritedStartOnMainThread(
        string sceneIdentity,
        SceneCameraState inheritedState);
}

public sealed record SceneCameraInheritedStartSnapshot(
    string SceneIdentity,
    SceneCameraState Before,
    SceneCameraState Applied,
    bool BaselineCaptured);

public interface ICharacterTransformCommandDispatcher
{
    ApiResult<CharacterTransformDispatchSnapshot> DispatchOnMainThread(
        CharacterTransformDispatchRequest request);

    ApiResult<CharacterTransformInheritedStartSnapshot> ApplyInheritedStartOnMainThread(
        string sceneIdentity,
        int publicSlot,
        string expectedOccupantIdentifier,
        PreviewChainSlotState inheritedStart);

    ApiResult<SlotPendingStoredSnapshot> StoreSlotPendingOnMainThread(
        string sceneIdentity,
        string canonicalDirective,
        long createdWindowSequence);

    ApiResult<IReadOnlyList<SlotPendingOutcome>> ScanSlotPendingsOnMainThread(
        string? currentSceneIdentity,
        long currentWindowSequence);

    ApiResult<bool> ClearSlotPendings();
}

public enum SlotPendingOutcomeStatus
{
    Applied = 0,
    Failed = 1,
    Expired = 2
}

public sealed record SlotPendingStoredSnapshot(
    string SceneIdentity,
    int PublicSlot,
    string CanonicalDirective,
    long CreatedWindowSequence);

public sealed record SlotPendingOutcome(
    int PublicSlot,
    SlotPendingOutcomeStatus Status,
    string? OccupantIdentifier,
    float? BeforeX,
    float? AppliedX,
    string? Reason);

public interface ICharacterActionService
{
    ApiResult<CharacterActionExecutionSnapshot> EnqueueOnMainThread(
        CharacterActionRequest request);
}

public interface IAzureArchiveApi
{
    ICapabilityService Capabilities { get; }
    IEditorSceneService Editor { get; }
    IEditorCommandDocumentService EditorCommandDocuments { get; }
    IEditorSceneIdentityService SceneIdentity { get; }
    IConfiguredSceneResolutionService SceneResolution { get; }
    IScenarioFileService Files { get; }
    IContinuousCompilerService ContinuousCompiler { get; }
    IScenarioPlaybackService Playback { get; }
    IPlayerRuntimeContextService PlayerContext { get; }
    IScreenTextRuntimeService ScreenTexts { get; }
    ICharacterTransformService CharacterTransforms { get; }
    ICharacterTransformCommandDispatcher CharacterCommands { get; }
    ISceneCameraService SceneCamera { get; }
    ICharacterActionService CharacterActions { get; }
}
