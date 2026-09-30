using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record PlaybackSceneDispatchAuthorization(
    long ExecutionOrdinal,
    long WindowSequence,
    int PlaybackRecordIndex,
    string SceneIdentity,
    IReadOnlyList<PlaybackCommandInstruction> Commands);

public sealed class PlaybackSceneDispatchGate
{
    private readonly object _gate = new();
    private long _lastAuthorizedWindowSequence;
    private long _executionOrdinal;

    public Result<PlaybackSceneDispatchAuthorization> TryAuthorize(
        long windowSequence,
        PlaybackCommandBatch batch)
    {
        if (windowSequence <= 0)
        {
            return Result<PlaybackSceneDispatchAuthorization>.Fail(
                "Scene dispatch window sequence must be positive.");
        }

        Result<ValidatedBatch> validated = ValidateBatch(batch);
        if (!validated.Success || validated.Value == null)
        {
            return Result<PlaybackSceneDispatchAuthorization>.Fail(
                validated.Error);
        }

        lock (_gate)
        {
            if (windowSequence <= _lastAuthorizedWindowSequence)
            {
                return Result<PlaybackSceneDispatchAuthorization>.Fail(
                    "Scene dispatch window was already consumed or arrived out of order.");
            }

            _lastAuthorizedWindowSequence = windowSequence;
            long ordinal = ++_executionOrdinal;
            return Result<PlaybackSceneDispatchAuthorization>.Ok(new(
                ordinal,
                windowSequence,
                batch.PlaybackRecordIndex,
                validated.Value.SceneIdentity,
                validated.Value.Commands));
        }
    }

    private static Result<ValidatedBatch> ValidateBatch(PlaybackCommandBatch batch)
    {
        if (batch?.Scene == null
            || batch.PlaybackRecordIndex < 0
            || batch.Commands == null
            || batch.Commands.Count is < 1 or > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene
            || !Guid.TryParseExact(batch.Scene.NodeGuid, "D", out _)
            || batch.Scene.SceneIndex < 0
            || !CommandIdentity.TryNormalizeSha256(batch.Scene.Fingerprint, out _))
        {
            return Result<ValidatedBatch>.Fail(
                "Scene dispatch batch is null or malformed.");
        }

        var transformCompiler = new CharacterTransformCommandFamilyCompiler();
        var slotPendingCompiler = new SlotPendingCommandFamilyCompiler();
        var cameraCompiler = new SceneCameraCommandFamilyCompiler();
        var spineCompiler = new SpineOverlayCommandFamilyCompiler();
        var presetCompiler = new CharacterPresetCommandFamilyCompiler();
        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new HashSet<string>(StringComparer.Ordinal);
        var commands = new PlaybackCommandInstruction[batch.Commands.Count];
        for (int index = 0; index < batch.Commands.Count; index++)
        {
            PlaybackCommandInstruction? command = batch.Commands[index];
            bool isTransformFamily = string.Equals(
                command?.CommandType,
                CharacterTransformCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal);
            bool isSlotPendingFamily = string.Equals(
                command?.CommandType,
                SlotPendingCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal);
            bool isCameraFamily = string.Equals(
                command?.CommandType,
                SceneCameraCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal);
            bool isSpineFamily = string.Equals(
                command?.CommandType,
                SpineOverlayCommandFamilyCompiler.CommandTypeId,
                StringComparison.Ordinal);
            bool isPresetFamily = command?.CommandType == CharacterPresetCommandFamilyCompiler.CommandTypeId;
            string expectedCapability = isPresetFamily
                ? CharacterPresetCommandFamilyCompiler.CapabilityId
                : isCameraFamily
                ? SceneCameraCommandFamilyCompiler.CapabilityId
                : isSpineFamily
                    ? SpineOverlayCommandFamilyCompiler.CapabilityId
                    : isSlotPendingFamily
                        ? SlotPendingCommandFamilyCompiler.CapabilityId
                        : CharacterTransformCommandFamilyCompiler.CapabilityId;
            if (command == null
                || (!isTransformFamily && !isSlotPendingFamily && !isCameraFamily && !isSpineFamily && !isPresetFamily)
                || !Guid.TryParseExact(command.CommandId, "D", out _)
                || !commandIds.Add(command.CommandId)
                || command.Order != index
                || command.Phase != CommandTimelinePhase.SceneEnter
                || !string.Equals(
                    command.RequiredCapability,
                    expectedCapability,
                    StringComparison.Ordinal))
            {
                return Result<ValidatedBatch>.Fail(
                    $"Scene dispatch command {index} is malformed or duplicated.");
            }

            ICommandFamilyCompiler family = isPresetFamily
                ? presetCompiler
                : isCameraFamily
                ? cameraCompiler
                : isSpineFamily
                    ? spineCompiler
                    : isSlotPendingFamily
                        ? slotPendingCompiler
                        : transformCompiler;
            Result<CanonicalTimelineCommand> canonical =
                family.Canonicalize(command.CanonicalDirective);
            if (!canonical.Success || canonical.Value == null
                || !string.Equals(
                    canonical.Value.Directive,
                    command.CanonicalDirective,
                    StringComparison.Ordinal)
                || !slots.Add(CommandResourceIdentity.KeyFor(canonical.Value)))
            {
                return Result<ValidatedBatch>.Fail(
                    $"Scene dispatch command {index} is non-canonical or conflicts with another slot.");
            }

            commands[index] = command with { };
        }

        if (!CommandResourceIdentity.IsWithinSceneBudget(commands.Select(command => command.CommandType)))
        {
            return Result<ValidatedBatch>.Fail("Scene dispatch batch exceeds the supported resource count.");
        }

        string sceneIdentity =
            $"playback-sidecar:{batch.Scene.NodeGuid}:{batch.Scene.SceneIndex}:{batch.Scene.Fingerprint}";
        return Result<ValidatedBatch>.Ok(new(
            sceneIdentity,
            Array.AsReadOnly(commands)));
    }

    private sealed record ValidatedBatch(
        string SceneIdentity,
        IReadOnlyList<PlaybackCommandInstruction> Commands);
}
