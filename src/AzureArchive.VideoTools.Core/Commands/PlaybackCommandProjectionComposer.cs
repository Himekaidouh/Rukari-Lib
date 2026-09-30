using System.Security.Cryptography;
using System.Text;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed class PlaybackCommandProjectionComposer
{
    private readonly CharacterTransformCommandFamilyCompiler _characterCompiler = new();
    private readonly SlotPendingCommandFamilyCompiler _slotPendingCompiler = new();
    private readonly SceneCameraCommandFamilyCompiler _cameraCompiler = new();
    private readonly SpineOverlayCommandFamilyCompiler _spineCompiler = new();
    private readonly CharacterPresetCommandFamilyCompiler _presetCompiler = new();

    public Result<PlaybackCommandProjection> Compose(
        PlaybackCommandProjection? sidecar,
        PlaybackCommandProjection embedded)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        if (sidecar == null || sidecar.Batches.Count == 0)
        {
            return Result<PlaybackCommandProjection>.Ok(embedded);
        }

        Result sources = ValidateSameSources(sidecar, embedded);
        if (!sources.Success)
        {
            return Result<PlaybackCommandProjection>.Fail(sources.Error);
        }

        var batches = sidecar.Batches.ToDictionary(
            batch => batch.PlaybackRecordIndex,
            Snapshot);
        foreach (PlaybackCommandBatch embeddedBatch in embedded.Batches)
        {
            if (!batches.TryGetValue(
                    embeddedBatch.PlaybackRecordIndex,
                    out PlaybackCommandBatch? sidecarBatch))
            {
                batches.Add(embeddedBatch.PlaybackRecordIndex, Snapshot(embeddedBatch));
                continue;
            }

            Result<PlaybackCommandBatch> merged = MergeBatch(sidecarBatch, embeddedBatch);
            if (!merged.Success || merged.Value == null)
            {
                return Result<PlaybackCommandProjection>.Fail(merged.Error);
            }

            batches[embeddedBatch.PlaybackRecordIndex] = merged.Value;
        }

        PlaybackCommandBatch[] ordered = batches.Values
            .OrderBy(batch => batch.PlaybackRecordIndex)
            .ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaybackCommandInstruction command in ordered.SelectMany(batch => batch.Commands))
        {
            if (!ids.Add(command.CommandId))
            {
                return Result<PlaybackCommandProjection>.Fail(
                    $"Composed command projection contains duplicate command ID {command.CommandId}.");
            }
        }

        string compositionIdentity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "AzureArchive.VideoTools/composed-projection/v1\n"
                + sidecar.SourceTimelineSha256 + "\n" + embedded.SourceTimelineSha256)));
        return Result<PlaybackCommandProjection>.Ok(embedded with
        {
            WorkspaceId = sidecar.WorkspaceId,
            SourceTimelineSha256 = compositionIdentity,
            Batches = Array.AsReadOnly(ordered)
        });
    }

    private Result<PlaybackCommandBatch> MergeBatch(
        PlaybackCommandBatch sidecar,
        PlaybackCommandBatch embedded)
    {
        if (!SameScene(sidecar, embedded)
            || !string.Equals(
                sidecar.PlaybackRecordFingerprint,
                embedded.PlaybackRecordFingerprint,
                StringComparison.OrdinalIgnoreCase)
            || sidecar.CompiledScript != embedded.CompiledScript)
        {
            return Result<PlaybackCommandBatch>.Fail(
                $"Embedded and sidecar commands disagree on playback record {embedded.PlaybackRecordIndex} identity.");
        }

        var combined = sidecar.Commands.Select(command => command with { }).ToList();
        foreach (PlaybackCommandInstruction command in embedded.Commands)
        {
            Result<string> slot = ReadResourceKey(command);
            if (!slot.Success)
            {
                return Result<PlaybackCommandBatch>.Fail(slot.Error);
            }

            int replacement = -1;
            for (int index = 0; index < combined.Count; index++)
            {
                Result<string> existingSlot = ReadResourceKey(combined[index]);
                if (!existingSlot.Success)
                {
                    return Result<PlaybackCommandBatch>.Fail(existingSlot.Error);
                }

                if (existingSlot.Value == slot.Value)
                {
                    replacement = index;
                    break;
                }
            }

            if (replacement >= 0)
            {
                combined[replacement] = command with { };
            }
            else
            {
                combined.Add(command with { });
            }
        }

        if (!CommandResourceIdentity.IsWithinSceneBudget(combined.Select(command => command.CommandType)))
        {
            return Result<PlaybackCommandBatch>.Fail(
                $"Composed playback record {embedded.PlaybackRecordIndex} exceeds the supported resource count.");
        }

        PlaybackCommandInstruction[] renumbered = combined
            .Select((command, index) => command with { Order = index })
            .ToArray();
        return Result<PlaybackCommandBatch>.Ok(embedded with
        {
            Commands = Array.AsReadOnly(renumbered)
        });
    }

    private Result<string> ReadResourceKey(PlaybackCommandInstruction command)
    {
        ICommandFamilyCompiler? family = command.CommandType switch
        {
            CharacterTransformCommandFamilyCompiler.CommandTypeId => _characterCompiler,
            SlotPendingCommandFamilyCompiler.CommandTypeId => _slotPendingCompiler,
            SceneCameraCommandFamilyCompiler.CommandTypeId => _cameraCompiler,
            SpineOverlayCommandFamilyCompiler.CommandTypeId => _spineCompiler,
            CharacterPresetCommandFamilyCompiler.CommandTypeId => _presetCompiler,
            _ => null
        };
        if (family == null)
        {
            return Result<string>.Fail(
                $"Command {command.CommandId} cannot be composed: unsupported command family.");
        }

        Result<CanonicalTimelineCommand> canonical =
            family.Canonicalize(command.CanonicalDirective);
        return canonical.Success && canonical.Value != null
            && command.RequiredCapability == canonical.Value.RequiredCapability
            && command.CanonicalDirective == canonical.Value.Directive
            ? Result<string>.Ok(CommandResourceIdentity.KeyFor(canonical.Value))
            : Result<string>.Fail(
                $"Command {command.CommandId} cannot be composed: {canonical.Error}");
    }

    private static Result ValidateSameSources(
        PlaybackCommandProjection left,
        PlaybackCommandProjection right)
    {
        bool same = string.Equals(left.ProjectPathKey, right.ProjectPathKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ProjectRevisionSha256, right.ProjectRevisionSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PlaybackPathKey, right.PlaybackPathKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PlaybackRevisionSha256, right.PlaybackRevisionSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PlaybackSchemaName, right.PlaybackSchemaName, StringComparison.Ordinal);
        return same
            ? Result.Ok()
            : Result.Fail("Embedded and sidecar command projections target different AAP/AAS sources.");
    }

    private static bool SameScene(
        PlaybackCommandBatch left,
        PlaybackCommandBatch right) =>
        left.PlaybackRecordIndex == right.PlaybackRecordIndex
        && string.Equals(left.Scene.NodeGuid, right.Scene.NodeGuid, StringComparison.OrdinalIgnoreCase)
        && left.Scene.SceneIndex == right.Scene.SceneIndex
        && string.Equals(left.Scene.Fingerprint, right.Scene.Fingerprint, StringComparison.OrdinalIgnoreCase);

    private static PlaybackCommandBatch Snapshot(PlaybackCommandBatch batch) => batch with
    {
        Scene = batch.Scene with { },
        CompiledScript = batch.CompiledScript with { },
        Commands = Array.AsReadOnly(batch.Commands.Select(command => command with { }).ToArray())
    };
}
