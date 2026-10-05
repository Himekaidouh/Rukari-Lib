using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;
using AzureArchive.VideoTools.Core.Spines;
using Rukari.Lib.Spines;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Live editor identity used by an unsaved preview lease. This deliberately
/// does not depend on a persisted AAP/AAS mapping.
/// </summary>
public sealed record EditorPreviewSceneAddress(
    string ProjectKey,
    string NodeGuid,
    int SceneIndex,
    string Fingerprint);

public sealed record EditorPreviewSelectionBinding(
    long SelectionGeneration,
    int SelectionRequestId,
    long ObservationSequence,
    CompiledScriptIdentity CompiledScript,
    EditorPreviewSceneAddress Scene);

/// <summary>
/// A captured editor compile candidate. An empty directive collection is
/// valid only when it is an explicit tombstone produced by a compile that
/// observed no AAVT directives.
/// </summary>
public sealed record EditorPreviewLeaseCandidate(
    EditorPreviewSelectionBinding Selection,
    IReadOnlyList<string> CanonicalDirectives,
    bool IsTombstone,
    long MinimumWindowSequenceExclusive = 0);

public sealed record EditorPreviewLiveObservation(
    PlayerRuntimeContextSnapshot RuntimeContext,
    EditorPreviewSelectionBinding Selection,
    int ExactCompiledScriptMessageCount,
    long WindowSequence = long.MaxValue);

public enum EditorPreviewDispatchMode
{
    Immediate = 0,
    DeferredSlotPending = 1
}

public enum EditorPreviewResourceKind
{
    CharacterSlot = 0,
    SceneCamera = 1,
    SpineOverlay = 2,
    CharacterPreset = 3
}

[Flags]
public enum EditorPreviewResourceFields
{
    None = 0,
    CharacterPositionX = 1 << 0,
    CharacterPositionY = 1 << 1,
    CharacterScreenRotation = 1 << 2,
    CharacterHorizontalFlip = 1 << 3,
    CameraPositionX = 1 << 4,
    CameraPositionY = 1 << 5,
    CameraZoom = 1 << 6,

    /// <summary>
    /// One reserved overlay track of one character. An overlay has no value to restore — leaving the
    /// track is a release — so this footprint is a reservation rather than an axis.
    /// </summary>
    SpineOverlayTrack = 1 << 7,
    CharacterPresetOverlay = 1 << 8,
    CharacterRotationX = 1 << 9
}

/// <summary>
/// One resource an editor preview command occupies. <paramref name="TrackIndex"/> is zero for
/// everything but a spine overlay, where it is what makes two overlays of one character into two
/// resources instead of a conflict.
/// </summary>
public readonly record struct EditorPreviewResourceKey(
    EditorPreviewResourceKind Kind,
    int PublicSlot,
    int TrackIndex = 0);

public sealed record EditorPreviewCommandPlan(
    int Order,
    string CommandType,
    string RequiredCapability,
    string CanonicalDirective,
    EditorPreviewResourceKey Resource,
    EditorPreviewResourceFields Fields,
    EditorPreviewDispatchMode DispatchMode)
{
    public bool DispatchImmediately => DispatchMode == EditorPreviewDispatchMode.Immediate;
}

public sealed record EditorPreviewResourceFootprintEntry(
    EditorPreviewResourceKey Resource,
    EditorPreviewResourceFields Fields,
    EditorPreviewDispatchMode DispatchMode);

public sealed record EditorPreviewResourceFootprint(
    string StableSceneIdentity,
    IReadOnlyList<EditorPreviewResourceFootprintEntry> Entries);

public sealed record EditorPreviewLeaseAuthorization(
    long SelectionGeneration,
    string StableSceneIdentity,
    bool IsTombstone,
    IReadOnlyList<EditorPreviewCommandPlan> Commands,
    EditorPreviewResourceFootprint Footprint);

/// <summary>
/// Gate for an unsaved editor preview. The original entry point consumes a
/// selection generation once; the window entry point permits verified replays
/// with new actual windows. Older generations cannot execute afterward.
/// </summary>
public sealed class EditorPreviewLeaseGate
{
    private readonly object _gate = new();
    private long _lastConsumedSelectionGeneration;
    private long _lastConsumedWindowSequence;

    public Result<EditorPreviewLeaseAuthorization> TryAuthorize(
        EditorPreviewLeaseCandidate candidate,
        EditorPreviewLiveObservation live)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(live);

        EditorPreviewSelectionBinding? expected = candidate.Selection;
        if (expected == null || expected.SelectionGeneration <= 0)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview lease has no positive selection generation.");
        }

        lock (_gate)
        {
            if (expected.SelectionGeneration <= _lastConsumedSelectionGeneration)
            {
                return Result<EditorPreviewLeaseAuthorization>.Fail(
                    "Editor preview lease was already consumed or arrived out of order.");
            }

            _lastConsumedSelectionGeneration = expected.SelectionGeneration;
            if (live.WindowSequence > _lastConsumedWindowSequence
                && live.WindowSequence < long.MaxValue)
            {
                // Real legacy windows also stay consumed when the caller
                // switches to the replay entry point. The historical default
                // remains a placeholder and does not reserve a window.
                _lastConsumedWindowSequence = live.WindowSequence;
            }
        }

        return ValidateAndPlan(candidate, live);
    }

    /// <summary>
    /// Authorizes a confirmed selection once per increasing actual window.
    /// A failed attempt consumes that window, while a later window may replay
    /// the same confirmed generation. The caller supplies the current
    /// generation's selection proof and an observed closed preview window.
    /// </summary>
    public Result<EditorPreviewLeaseAuthorization> TryAuthorizeWindow(
        EditorPreviewLeaseCandidate candidate,
        EditorPreviewLiveObservation live)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(live);

        EditorPreviewSelectionBinding? expected = candidate.Selection;
        if (expected == null || expected.SelectionGeneration <= 0)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview lease has no positive selection generation.");
        }

        // The legacy default is a placeholder, never a real window sequence.
        if (live.WindowSequence <= 0 || live.WindowSequence == long.MaxValue)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview replay requires an actual positive window sequence.");
        }

        lock (_gate)
        {
            if (expected.SelectionGeneration < _lastConsumedSelectionGeneration
                || live.WindowSequence <= _lastConsumedWindowSequence)
            {
                return Result<EditorPreviewLeaseAuthorization>.Fail(
                    "Editor preview window was already consumed or arrived out of order.");
            }

            // Share the generation watermark with the legacy entry point so
            // switching APIs cannot revive a superseded selection. Window
            // sequences are global and never reset when DataList advances.
            _lastConsumedSelectionGeneration = expected.SelectionGeneration;
            _lastConsumedWindowSequence = live.WindowSequence;
        }

        if (expected.ObservationSequence <= 0)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview replay requires a confirmed selection observation.");
        }

        return ValidateAndPlan(candidate, live);
    }

    private static Result<EditorPreviewLeaseAuthorization> ValidateAndPlan(
        EditorPreviewLeaseCandidate candidate,
        EditorPreviewLiveObservation live)
    {
        EditorPreviewSelectionBinding expected = candidate.Selection;
        PlayerRuntimeContextSnapshot? context = live.RuntimeContext;
        EditorPreviewSelectionBinding? actual = live.Selection;
        if (context == null
            || !context.PlayerAvailable
            || !context.PreviewMode
            || context.Mode != PlayerRuntimeMode.EditorPreview)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview leases may execute only in PlayerRuntimeMode.EditorPreview.");
        }

        Result expectedValidation = ValidateSelection(expected, "captured");
        if (!expectedValidation.Success)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(expectedValidation.Error);
        }

        // A captured ObservationSequence of 0 means "selection event not
        // required": the generation was never confirmed by OnChildSelect.
        // The live side must still carry a positive observation sequence.
        Result actualValidation = ValidateSelection(actual, "live");
        if (!actualValidation.Success)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(actualValidation.Error);
        }

        if (!SameSelection(expected, actual!))
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview selection generation, request, observation, compiled script, or live scene address drifted.");
        }

        if (live.ExactCompiledScriptMessageCount != 1)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview window must contain the exact compiled script once.");
        }

        if (candidate.MinimumWindowSequenceExclusive < 0
            || live.WindowSequence <= candidate.MinimumWindowSequenceExclusive)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(
                "Editor preview window predates the current selection lease.");
        }

        Result<IReadOnlyList<EditorPreviewCommandPlan>> commandsResult =
            ValidateCommands(candidate.CanonicalDirectives, candidate.IsTombstone);
        if (!commandsResult.Success || commandsResult.Value == null)
        {
            return Result<EditorPreviewLeaseAuthorization>.Fail(commandsResult.Error);
        }

        string sceneIdentity = StableSceneIdentity(expected.Scene);
        EditorPreviewCommandPlan[] commands = commandsResult.Value.ToArray();
        EditorPreviewResourceFootprintEntry[] entries = commands
            .Select(command => new EditorPreviewResourceFootprintEntry(
                command.Resource,
                command.Fields,
                command.DispatchMode))
            .ToArray();
        var footprint = new EditorPreviewResourceFootprint(
            sceneIdentity,
            Array.AsReadOnly(entries));
        return Result<EditorPreviewLeaseAuthorization>.Ok(new(
            expected.SelectionGeneration,
            sceneIdentity,
            candidate.IsTombstone,
            Array.AsReadOnly(commands),
            footprint));
    }

    private static Result ValidateSelection(
        EditorPreviewSelectionBinding? selection,
        string label)
    {
        if (selection?.CompiledScript == null || selection.Scene == null
            || selection.SelectionGeneration <= 0
            || selection.SelectionRequestId < 0
            || selection.ObservationSequence < 0)
        {
            return Result.Fail(
                $"The {label} editor preview selection is null or has invalid sequence values.");
        }

        if (string.Equals(label, "live", StringComparison.Ordinal)
            && selection.ObservationSequence <= 0)
        {
            return Result.Fail(
                $"The {label} editor preview selection is null or has invalid sequence values.");
        }

        CompiledScriptIdentity script = selection.CompiledScript;
        if (!CommandIdentity.TryNormalizeSha256(script.Sha256, out _)
            || script.Utf16Length < 0
            || script.LineCount < 1)
        {
            return Result.Fail(
                $"The {label} editor preview compiled-script identity is invalid.");
        }

        EditorPreviewSceneAddress scene = selection.Scene;
        if (!IsLiveSceneFingerprint(scene.ProjectKey)
            || !Guid.TryParseExact(scene.NodeGuid, "D", out _)
            || scene.SceneIndex < 0
            || !IsLiveSceneFingerprint(scene.Fingerprint))
        {
            return Result.Fail(
                $"The {label} live editor scene address is invalid.");
        }

        return Result.Ok();
    }

    private static bool IsLiveSceneFingerprint(string value)
    {
        if (value == null || value.Length != 24)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Contract relaxation: an EXPECTED ObservationSequence of 0 means the
    /// generation was never confirmed by OnChildSelect (the green replay
    /// button), so the live identity — which still describes the last card
    /// selection — cannot be compared by generation/request/observation at
    /// all. Those leases are validated by compiled-script and scene equality
    /// plus the claim-time candidate intersection. A confirmed lease keeps
    /// full field-by-field equality.
    /// </summary>
    private static bool SameSelection(
        EditorPreviewSelectionBinding expected,
        EditorPreviewSelectionBinding actual)
    {
        if (expected.CompiledScript != actual.CompiledScript
            || expected.Scene != actual.Scene)
        {
            return false;
        }

        return expected.ObservationSequence == 0
            || (expected.SelectionGeneration == actual.SelectionGeneration
                && expected.SelectionRequestId == actual.SelectionRequestId
                && expected.ObservationSequence == actual.ObservationSequence);
    }

    private static Result<IReadOnlyList<EditorPreviewCommandPlan>> ValidateCommands(
        IReadOnlyList<string>? directives,
        bool isTombstone)
    {
        if (directives == null
            || directives.Count > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene)
        {
            return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                "Editor preview lease must contain zero to "
                + $"{EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene} canonical directives.");
        }

        if (directives.Count == 0)
        {
            return isTombstone
                ? Result<IReadOnlyList<EditorPreviewCommandPlan>>.Ok(
                    Array.Empty<EditorPreviewCommandPlan>())
                : Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                    "An empty editor preview lease must be an explicit tombstone.");
        }

        if (isTombstone)
        {
            return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                "An editor preview tombstone cannot contain directives.");
        }

        var characterCompiler = new CharacterTransformCommandFamilyCompiler();
        var pendingCompiler = new SlotPendingCommandFamilyCompiler();
        var cameraCompiler = new SceneCameraCommandFamilyCompiler();
        var spineCompiler = new SpineOverlayCommandFamilyCompiler();
        var presetCompiler = new CharacterPresetCommandFamilyCompiler();
        var resources = new HashSet<EditorPreviewResourceKey>();
        var commands = new EditorPreviewCommandPlan[directives.Count];
        int characterResourceCount = 0;
        int cameraResourceCount = 0;
        int spineResourceCount = 0;
        int presetResourceCount = 0;
        for (int index = 0; index < directives.Count; index++)
        {
            string? directive = directives[index];
            ICommandFamilyCompiler family;
            EditorPreviewDispatchMode dispatchMode;
            if (directive?.StartsWith("#char;", StringComparison.Ordinal) == true)
            {
                family = characterCompiler;
                dispatchMode = EditorPreviewDispatchMode.Immediate;
            }
            else if (directive?.StartsWith(
                         SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal) == true)
            {
                family = pendingCompiler;
                dispatchMode = EditorPreviewDispatchMode.DeferredSlotPending;
            }
            else if (directive?.StartsWith(
                         SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal) == true)
            {
                family = cameraCompiler;
                dispatchMode = EditorPreviewDispatchMode.Immediate;
            }
            else if (directive?.StartsWith(
                         SpineOverlayCommandFamilyCompiler.CanonicalRootToken + ";",
                         StringComparison.Ordinal) == true)
            {
                family = spineCompiler;
                dispatchMode = EditorPreviewDispatchMode.Immediate;
            }
            else if (directive?.StartsWith(CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";", StringComparison.Ordinal) == true)
            {
                family = presetCompiler;
                dispatchMode = EditorPreviewDispatchMode.Immediate;
            }
            else
            {
                return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                    $"Editor preview directive {index} has an unknown or non-canonical command family.");
            }

            Result<CanonicalTimelineCommand> canonical = family.Canonicalize(directive);
            if (!canonical.Success || canonical.Value == null
                || !string.Equals(
                    directive,
                    canonical.Value.Directive,
                    StringComparison.Ordinal))
            {
                return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                    $"Editor preview directive {index} is not canonical.");
            }

            CanonicalTimelineCommand normalized = canonical.Value;
            bool isCamera = string.Equals(
                normalized.CommandType,
                SceneCameraCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal);
            bool isSpine = string.Equals(
                normalized.CommandType,
                SpineOverlayCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal);
            bool isPreset = normalized.CommandType == CharacterPresetCommandFamilyCompiler.CommandTypeId;
            int spineTrack = 0;
            if (isSpine)
            {
                // Two overlays of one character are two resources when their tracks differ, so the
                // track is part of the key; a track that cannot be read is not a resource at all.
                spineTrack = CommandResourceIdentity.SpineTrackOf(normalized.Directive);
                if (spineTrack < 0)
                {
                    return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                        $"Editor preview directive {index} names no readable overlay track.");
                }
            }

            var resource = new EditorPreviewResourceKey(
                isCamera
                    ? EditorPreviewResourceKind.SceneCamera
                    : isSpine
                        ? EditorPreviewResourceKind.SpineOverlay
                        : isPreset ? EditorPreviewResourceKind.CharacterPreset
                        : EditorPreviewResourceKind.CharacterSlot,
                normalized.PublicSlot,
                spineTrack);
            if (!resources.Add(resource))
            {
                return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                    $"Editor preview directive {index} conflicts with another command resource.");
            }

            if (isCamera)
            {
                cameraResourceCount++;
            }
            else if (isSpine)
            {
                spineResourceCount++;
            }
            else if (isPreset)
            {
                presetResourceCount++;
            }
            else
            {
                characterResourceCount++;
            }

            Result<EditorPreviewResourceFields> fields = FieldsFor(
                normalized.CommandType,
                normalized.Directive);
            if (!fields.Success)
            {
                return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(fields.Error);
            }

            commands[index] = new EditorPreviewCommandPlan(
                index,
                normalized.CommandType,
                normalized.RequiredCapability,
                normalized.Directive,
                resource,
                fields.Value,
                dispatchMode);
        }

        if (presetResourceCount > 5
            || characterResourceCount > 5
            || cameraResourceCount > 2
            || spineResourceCount > SpineOverlayTracks.Last - SpineOverlayTracks.First + 1)
        {
            return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Fail(
                "Editor preview lease allows at most five character slots, five transient presets, "
                + $"{SpineOverlayTracks.Last - SpineOverlayTracks.First + 1} spine overlay tracks "
                + "and two independent camera scope resources.");
        }

        return Result<IReadOnlyList<EditorPreviewCommandPlan>>.Ok(
            Array.AsReadOnly(commands));
    }

    private static Result<EditorPreviewResourceFields> FieldsFor(
        string commandType,
        string canonicalDirective)
    {
        if (commandType == CharacterPresetCommandFamilyCompiler.CommandTypeId)
        {
            var preset = new CharacterPresetCommandFamilyCompiler().Canonicalize(canonicalDirective);
            return preset.Success
                ? Result<EditorPreviewResourceFields>.Ok(EditorPreviewResourceFields.CharacterPresetOverlay)
                : Result<EditorPreviewResourceFields>.Fail(preset.Error);
        }

        if (string.Equals(
                commandType,
                SpineOverlayCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal))
        {
            // An overlay has no restorable axis: its footprint is the reservation of one track.
            // Leaving the track is a release, which the runtime plans from the resource itself.
            Result<SpineOverlayCommand> parsed =
                new SpineOverlayDirectiveParser().Parse(canonicalDirective);
            return parsed.Success && parsed.Value != null
                ? Result<EditorPreviewResourceFields>.Ok(
                    EditorPreviewResourceFields.SpineOverlayTrack)
                : Result<EditorPreviewResourceFields>.Fail(parsed.Error);
        }

        if (string.Equals(
                commandType,
                SceneCameraCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal))
        {
            var parsed = new Cameras.SceneCameraDirectiveParser().Parse(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<EditorPreviewResourceFields>.Fail(parsed.Error);
            }

            Cameras.SceneCameraCommand command = parsed.Value;
            EditorPreviewResourceFields fields = command.Operation switch
            {
                Cameras.SceneCameraOperation.Set =>
                    (command.X.HasValue ? EditorPreviewResourceFields.CameraPositionX : 0)
                    | (command.Y.HasValue ? EditorPreviewResourceFields.CameraPositionY : 0)
                    | (command.Zoom.HasValue ? EditorPreviewResourceFields.CameraZoom : 0),
                Cameras.SceneCameraOperation.Move =>
                    (command.DeltaX.HasValue ? EditorPreviewResourceFields.CameraPositionX : 0)
                    | (command.DeltaY.HasValue ? EditorPreviewResourceFields.CameraPositionY : 0)
                    | (command.DeltaZoom.HasValue ? EditorPreviewResourceFields.CameraZoom : 0),
                Cameras.SceneCameraOperation.Reset => CameraFields,
                _ => EditorPreviewResourceFields.None
            };
            return fields == EditorPreviewResourceFields.None
                ? Result<EditorPreviewResourceFields>.Fail(
                    "Editor preview camera command has no resource footprint.")
                : Result<EditorPreviewResourceFields>.Ok(fields);
        }

        CharacterTransformCommand parsedCharacter;
        if (string.Equals(
                commandType,
                SlotPendingCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal))
        {
            Result<CharacterTransformCommand> parsed =
                new SlotPendingCommandFamilyCompiler().ParseCanonical(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<EditorPreviewResourceFields>.Fail(parsed.Error);
            }

            parsedCharacter = parsed.Value;
        }
        else
        {
            Result<CharacterTransformCommand> parsed =
                new CharacterTransformDirectiveParser().Parse(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<EditorPreviewResourceFields>.Fail(parsed.Error);
            }

            parsedCharacter = parsed.Value;
        }

        EditorPreviewResourceFields characterFields = parsedCharacter.Operation switch
        {
            CharacterTransformOperation.Set =>
                (parsedCharacter.X.HasValue ? EditorPreviewResourceFields.CharacterPositionX : 0)
                | (parsedCharacter.Y.HasValue ? EditorPreviewResourceFields.CharacterPositionY : 0)
                | (parsedCharacter.RotationDegrees.HasValue
                    ? EditorPreviewResourceFields.CharacterScreenRotation
                    : 0)
                | (parsedCharacter.RotationXDegrees.HasValue
                    ? EditorPreviewResourceFields.CharacterRotationX : 0)
                | (parsedCharacter.FlipX.HasValue
                    ? EditorPreviewResourceFields.CharacterHorizontalFlip
                    : 0),
            CharacterTransformOperation.Move =>
                (parsedCharacter.DeltaX.HasValue
                    ? EditorPreviewResourceFields.CharacterPositionX
                    : 0)
                | (parsedCharacter.DeltaY.HasValue
                    ? EditorPreviewResourceFields.CharacterPositionY
                    : 0)
                | (parsedCharacter.DeltaRotationDegrees.HasValue
                    ? EditorPreviewResourceFields.CharacterScreenRotation
                    : 0)
                | (parsedCharacter.DeltaRotationXDegrees.HasValue
                    ? EditorPreviewResourceFields.CharacterRotationX : 0),
            CharacterTransformOperation.Reset => CharacterFields,
            _ => EditorPreviewResourceFields.None
        };
        return characterFields == EditorPreviewResourceFields.None
            ? Result<EditorPreviewResourceFields>.Fail(
                "Editor preview character command has no resource footprint.")
            : Result<EditorPreviewResourceFields>.Ok(characterFields);
    }

    private const EditorPreviewResourceFields CharacterFields =
        EditorPreviewResourceFields.CharacterPositionX
        | EditorPreviewResourceFields.CharacterPositionY
        | EditorPreviewResourceFields.CharacterScreenRotation
        | EditorPreviewResourceFields.CharacterRotationX
        | EditorPreviewResourceFields.CharacterHorizontalFlip;

    private const EditorPreviewResourceFields CameraFields =
        EditorPreviewResourceFields.CameraPositionX
        | EditorPreviewResourceFields.CameraPositionY
        | EditorPreviewResourceFields.CameraZoom;

    public static string StableSceneIdentity(EditorPreviewSceneAddress scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        string projectHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(scene.ProjectKey)));
        string nodeGuid = Guid.ParseExact(scene.NodeGuid, "D").ToString("D");
        string fingerprint = scene.Fingerprint.ToUpperInvariant();
        return $"editor-preview:{projectHash}:{nodeGuid}:{scene.SceneIndex}:{fingerprint}";
    }
}

public sealed record EditorPreviewResourceTransition(
    EditorPreviewResourceKey Resource,
    EditorPreviewResourceFields PreviousFields,
    EditorPreviewResourceFields CurrentFields,
    EditorPreviewResourceFields UnionFields,
    EditorPreviewResourceFields RemovedFields,
    EditorPreviewDispatchMode? PreviousDispatchMode,
    EditorPreviewDispatchMode? CurrentDispatchMode,
    bool RestorePreviousImmediate,
    bool ClearPreviousSlotPending,
    bool ApplyCurrentImmediately,
    bool StoreCurrentSlotPending,
    bool CommandRemoved);

public sealed record EditorPreviewResourceCleanupPlan(
    string StableSceneIdentity,
    IReadOnlyList<EditorPreviewResourceTransition> Resources);

/// <summary>
/// Plans a transition from the last applied editor overlay to a new one. The
/// resource and field unions make deletion explicit: runtime code can restore
/// every previously applied immediate field, clear an old slot-pending entry,
/// and then apply/store the new command without relative transforms accruing.
/// </summary>
public sealed class EditorPreviewResourceCleanupPlanner
{
    public Result<EditorPreviewResourceCleanupPlan> Plan(
        EditorPreviewResourceFootprint? previous,
        EditorPreviewResourceFootprint current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>
            currentValidation = ValidateFootprint(current, "current");
        if (!currentValidation.Success || currentValidation.Value == null)
        {
            return Result<EditorPreviewResourceCleanupPlan>.Fail(
                currentValidation.Error);
        }

        IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>
            previousEntries = new Dictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>();
        if (previous != null)
        {
            Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>
                previousValidation = ValidateFootprint(previous, "previous");
            if (!previousValidation.Success || previousValidation.Value == null)
            {
                return Result<EditorPreviewResourceCleanupPlan>.Fail(
                    previousValidation.Error);
            }

            if (!string.Equals(
                    previous.StableSceneIdentity,
                    current.StableSceneIdentity,
                    StringComparison.Ordinal))
            {
                return Result<EditorPreviewResourceCleanupPlan>.Fail(
                    "Editor preview cleanup footprints target different stable scenes.");
            }

            previousEntries = previousValidation.Value;
        }

        IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>
            currentEntries = currentValidation.Value;
        EditorPreviewResourceKey[] resources = previousEntries.Keys
            .Concat(currentEntries.Keys)
            .Distinct()
            .OrderBy(resource => resource.Kind)
            .ThenBy(resource => resource.PublicSlot)
            .ThenBy(resource => resource.TrackIndex)
            .ToArray();
        var transitions = new EditorPreviewResourceTransition[resources.Length];
        for (int index = 0; index < resources.Length; index++)
        {
            EditorPreviewResourceKey resource = resources[index];
            previousEntries.TryGetValue(
                resource,
                out EditorPreviewResourceFootprintEntry? before);
            currentEntries.TryGetValue(
                resource,
                out EditorPreviewResourceFootprintEntry? after);
            EditorPreviewResourceFields beforeFields =
                before?.Fields ?? EditorPreviewResourceFields.None;
            EditorPreviewResourceFields afterFields =
                after?.Fields ?? EditorPreviewResourceFields.None;
            transitions[index] = new EditorPreviewResourceTransition(
                resource,
                beforeFields,
                afterFields,
                beforeFields | afterFields,
                beforeFields & ~afterFields,
                before?.DispatchMode,
                after?.DispatchMode,
                before?.DispatchMode == EditorPreviewDispatchMode.Immediate,
                before?.DispatchMode == EditorPreviewDispatchMode.DeferredSlotPending,
                after?.DispatchMode == EditorPreviewDispatchMode.Immediate,
                after?.DispatchMode == EditorPreviewDispatchMode.DeferredSlotPending,
                before != null && after == null);
        }

        return Result<EditorPreviewResourceCleanupPlan>.Ok(new(
            current.StableSceneIdentity,
            Array.AsReadOnly(transitions)));
    }

    private static Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>
        ValidateFootprint(EditorPreviewResourceFootprint footprint, string label)
    {
        if (string.IsNullOrWhiteSpace(footprint.StableSceneIdentity)
            || footprint.Entries == null)
        {
            return Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>.Fail(
                $"The {label} editor preview footprint is malformed.");
        }

        var entries = new Dictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>();
        foreach (EditorPreviewResourceFootprintEntry? entry in footprint.Entries)
        {
            if (entry == null
                || !Enum.IsDefined(entry.DispatchMode)
                || entry.Fields == EditorPreviewResourceFields.None
                || entries.ContainsKey(entry.Resource))
            {
                return Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>.Fail(
                    $"The {label} editor preview footprint contains an invalid or duplicate resource.");
            }

            bool validCharacter = entry.Resource.Kind == EditorPreviewResourceKind.CharacterSlot
                && entry.Resource.PublicSlot is >= 1 and <= 5
                && (entry.Fields & ~CharacterFields) == 0;
            bool validCamera = entry.Resource.Kind == EditorPreviewResourceKind.SceneCamera
                && SceneCameraCommandFamilyCompiler.IsCameraResourceSlot(entry.Resource.PublicSlot)
                && entry.DispatchMode == EditorPreviewDispatchMode.Immediate
                && (entry.Fields & ~CameraFields) == 0;
            bool validSpine = entry.Resource.Kind == EditorPreviewResourceKind.SpineOverlay
                && entry.Resource.PublicSlot is >= SpineOverlayTracks.FirstPublicSlot
                    and <= SpineOverlayTracks.LastPublicSlot
                && SpineOverlayTracks.IsReserved(entry.Resource.TrackIndex)
                && entry.DispatchMode == EditorPreviewDispatchMode.Immediate
                && entry.Fields == EditorPreviewResourceFields.SpineOverlayTrack;
            bool validPreset = entry.Resource.Kind == EditorPreviewResourceKind.CharacterPreset
                && entry.Resource.PublicSlot is >= 1 and <= 5
                && entry.Resource.TrackIndex == 0
                && entry.DispatchMode == EditorPreviewDispatchMode.Immediate
                && entry.Fields == EditorPreviewResourceFields.CharacterPresetOverlay;
            if (!validCharacter && !validCamera && !validSpine && !validPreset)
            {
                return Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>.Fail(
                    $"The {label} editor preview footprint contains an invalid resource shape.");
            }

            entries.Add(entry.Resource, entry);
        }

        return Result<IReadOnlyDictionary<EditorPreviewResourceKey, EditorPreviewResourceFootprintEntry>>.Ok(entries);
    }

    private const EditorPreviewResourceFields CharacterFields =
        EditorPreviewResourceFields.CharacterPositionX
        | EditorPreviewResourceFields.CharacterPositionY
        | EditorPreviewResourceFields.CharacterScreenRotation
        | EditorPreviewResourceFields.CharacterRotationX
        | EditorPreviewResourceFields.CharacterHorizontalFlip;

    private const EditorPreviewResourceFields CameraFields =
        EditorPreviewResourceFields.CameraPositionX
        | EditorPreviewResourceFields.CameraPositionY
        | EditorPreviewResourceFields.CameraZoom;
}
