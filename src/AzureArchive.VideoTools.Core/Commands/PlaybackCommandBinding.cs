using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed class PlaybackCommandBinder : IPlaybackCommandBinder
{
    private readonly IReadOnlyDictionary<string, ICommandFamilyCompiler> _families;

    public PlaybackCommandBinder(IEnumerable<ICommandFamilyCompiler>? families = null)
    {
        ICommandFamilyCompiler[] available = (families
                ?? new ICommandFamilyCompiler[]
                {
                    new CharacterTransformCommandFamilyCompiler(),
                    new SlotPendingCommandFamilyCompiler(),
                    new SceneCameraCommandFamilyCompiler(),
                    new SpineOverlayCommandFamilyCompiler(),
                    new CharacterPresetCommandFamilyCompiler()
                })
            .ToArray();
        if (available.Any(family => family == null))
        {
            throw new ArgumentException("Command family collection contains null.", nameof(families));
        }

        try
        {
            _families = available.ToDictionary(
                family => family.CommandType,
                StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "Command family types must be unique.",
                nameof(families),
                exception);
        }
    }

    public Result<IPlaybackCommandIndex> Bind(
        PlaybackArchiveSnapshot playback,
        PlaybackCommandProjection? projection)
    {
        if (playback?.Source == null || playback.Records == null)
        {
            return Result<IPlaybackCommandIndex>.Fail(
                "Playback snapshot has no source identity or record collection.");
        }

        if (!CommandIdentity.TryNormalizeSha256(playback.Source.PathKey, out _)
            || !CommandIdentity.TryNormalizeSha256(
                playback.Source.RevisionSha256,
                out _)
            || string.IsNullOrWhiteSpace(playback.SchemaName))
        {
            return Result<IPlaybackCommandIndex>.Fail(
                "Playback snapshot identity or schema is invalid.");
        }

        Result<PlaybackIndexState> playbackState = BuildPlaybackState(playback);
        if (!playbackState.Success || playbackState.Value == null)
        {
            return Result<IPlaybackCommandIndex>.Fail(playbackState.Error);
        }

        var batchesByRecord = new Dictionary<int, PlaybackCommandBatch>();
        if (projection != null)
        {
            Result projectionValidation = ValidateProjection(
                playback,
                projection,
                playbackState.Value,
                batchesByRecord);
            if (!projectionValidation.Success)
            {
                return Result<IPlaybackCommandIndex>.Fail(projectionValidation.Error);
            }
        }

        IPlaybackCommandIndex index = new PlaybackCommandIndex(
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            playbackState.Value.RecordsByIdentity,
            playbackState.Value.Fingerprints,
            batchesByRecord);
        return Result<IPlaybackCommandIndex>.Ok(index);
    }

    private Result ValidateProjection(
        PlaybackArchiveSnapshot playback,
        PlaybackCommandProjection projection,
        PlaybackIndexState playbackState,
        IDictionary<int, PlaybackCommandBatch> batchesByRecord)
    {
        if (projection.SchemaVersion != PlaybackCommandProjection.CurrentSchemaVersion
            || !Guid.TryParseExact(projection.WorkspaceId, "D", out _)
            || !CommandIdentity.TryNormalizeSha256(
                projection.SourceTimelineSha256,
                out _)
            || !CommandIdentity.TryNormalizeSha256(projection.ProjectPathKey, out _)
            || !CommandIdentity.TryNormalizeSha256(
                projection.ProjectRevisionSha256,
                out _)
            || projection.Batches == null)
        {
            return Result.Fail(
                "Command projection schema, identity, or batch collection is invalid.");
        }

        if (!SameSha(playback.Source.PathKey, projection.PlaybackPathKey)
            || !SameSha(
                playback.Source.RevisionSha256,
                projection.PlaybackRevisionSha256)
            || !string.Equals(
                playback.SchemaName,
                projection.PlaybackSchemaName,
                StringComparison.Ordinal))
        {
            return Result.Fail(
                "Command projection is not bound to the current playback path, revision, and schema.");
        }

        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int previousRecordIndex = -1;
        for (int batchIndex = 0; batchIndex < projection.Batches.Count; batchIndex++)
        {
            PlaybackCommandBatch? batch = projection.Batches[batchIndex];
            if (batch?.Scene == null
                || batch.Commands == null
                || batch.Commands.Count is < 1 or > EmbeddedAavtDirectiveExtractor.MaxCommandsPerScene
                || !Guid.TryParseExact(batch.Scene.NodeGuid, "D", out _)
                || batch.Scene.SceneIndex < 0
                || !CommandIdentity.TryNormalizeSha256(batch.Scene.Fingerprint, out _))
            {
                return Result.Fail(
                    $"Command projection batch {batchIndex} is null or malformed.");
            }

            int recordIndex = batch.PlaybackRecordIndex;
            if (recordIndex <= previousRecordIndex
                || recordIndex < 0
                || recordIndex >= playbackState.Identities.Length)
            {
                return Result.Fail(
                    "Command projection batches must target valid records in strictly increasing order.");
            }

            if (!SameSha(
                    batch.PlaybackRecordFingerprint,
                    playbackState.Fingerprints[recordIndex]))
            {
                return Result.Fail(
                    $"Command projection batch for playback record {recordIndex} has a stale fingerprint.");
            }

            Result<CompiledIdentityKey> batchIdentity = NormalizeIdentity(
                batch.CompiledScript,
                $"command projection batch {batchIndex}");
            if (!batchIdentity.Success)
            {
                return Result.Fail(batchIdentity.Error);
            }

            CompiledIdentityKey actualIdentity = IdentityKey(
                playbackState.Identities[recordIndex]);
            if (batchIdentity.Value != actualIdentity)
            {
                return Result.Fail(
                    $"Command projection batch for playback record {recordIndex} has a stale compiled-script identity.");
            }

            // 2026-09-18: a duplicate compiled-script identity is no longer a
            // hard failure. Cards with identical dialogue — empty camera-only
            // cards above all — share one identity, and the engine row index
            // observed in the advance window resolves which record is playing
            // (PlaybackCommandIndex.Observe + ObservedCompiledSceneIdentity.
            // PlaybackRowIndex). Batches are therefore keyed by record, not by
            // identity, and an unresolvable duplicate still fails closed at
            // runtime instead of disabling the whole archive.

            var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
            for (int commandIndex = 0; commandIndex < batch.Commands.Count; commandIndex++)
            {
                PlaybackCommandInstruction? command = batch.Commands[commandIndex];
                if (command == null
                    || !Guid.TryParseExact(command.CommandId, "D", out _)
                    || !commandIds.Add(command.CommandId)
                    || command.Order != commandIndex
                    || command.Phase != CommandTimelinePhase.SceneEnter
                    || !_families.TryGetValue(
                        command.CommandType,
                        out ICommandFamilyCompiler? family))
                {
                    return Result.Fail(
                        $"Command projection instruction {batchIndex}:{commandIndex} is malformed or duplicated.");
                }

                if (!string.Equals(
                        command.RequiredCapability,
                        family.RequiredCapability,
                        StringComparison.Ordinal))
                {
                    return Result.Fail(
                        $"Command {command.CommandId} declares the wrong runtime capability.");
                }

                Result<CanonicalTimelineCommand> canonical =
                    family.Canonicalize(command.CanonicalDirective);
                if (!canonical.Success || canonical.Value == null
                    || !string.Equals(
                        canonical.Value.Directive,
                        command.CanonicalDirective,
                        StringComparison.Ordinal)
                    || !occupiedSlots.Add(CommandResourceIdentity.KeyFor(canonical.Value)))
                {
                    return Result.Fail(
                        $"Command {command.CommandId} is non-canonical or conflicts with another slot command.");
                }
            }

            if (!CommandResourceIdentity.IsWithinSceneBudget(batch.Commands.Select(command => command.CommandType)))
            {
                return Result.Fail("Command projection batch exceeds the supported resource count.");
            }

            if (!batchesByRecord.TryAdd(recordIndex, Snapshot(batch)))
            {
                return Result.Fail(
                    "Command projection contains two batches for one playback record.");
            }

            previousRecordIndex = recordIndex;
        }

        return Result.Ok();
    }

    private static PlaybackCommandBatch Snapshot(PlaybackCommandBatch batch)
    {
        PlaybackCommandInstruction[] commands = batch.Commands
            .Select(command => command with { })
            .ToArray();
        return batch with
        {
            Scene = batch.Scene with { },
            CompiledScript = batch.CompiledScript with { },
            Commands = Array.AsReadOnly(commands)
        };
    }

    private static Result<PlaybackIndexState> BuildPlaybackState(
        PlaybackArchiveSnapshot playback)
    {
        var identities = new CompiledScriptIdentity[playback.Records.Count];
        var fingerprints = new string[playback.Records.Count];
        var recordsByIdentity = new Dictionary<CompiledIdentityKey, List<int>>();
        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || record.CompiledScript == null
                || !CommandIdentity.TryNormalizeSha256(record.Fingerprint, out _))
            {
                return Result<PlaybackIndexState>.Fail(
                    $"Playback record {index} has an invalid index, script, or fingerprint.");
            }

            CompiledScriptIdentity identity =
                CommandIdentity.CompiledScript(record.CompiledScript);
            CompiledIdentityKey key = IdentityKey(identity);
            if (!recordsByIdentity.TryGetValue(key, out List<int>? records))
            {
                records = new List<int>();
                recordsByIdentity.Add(key, records);
            }

            records.Add(index);
            identities[index] = identity;
            fingerprints[index] = record.Fingerprint;
        }

        return Result<PlaybackIndexState>.Ok(new PlaybackIndexState(
            identities,
            fingerprints,
            recordsByIdentity));
    }

    private static Result<CompiledIdentityKey> NormalizeIdentity(
        CompiledScriptIdentity? identity,
        string label)
    {
        if (identity == null
            || !CommandIdentity.TryNormalizeSha256(
                identity.Sha256,
                out string normalized)
            || identity.Utf16Length < 0
            || identity.LineCount < 1)
        {
            return Result<CompiledIdentityKey>.Fail(
                $"The {label} has an invalid compiled-script identity.");
        }

        return Result<CompiledIdentityKey>.Ok(new CompiledIdentityKey(
            normalized,
            identity.Utf16Length,
            identity.LineCount));
    }

    private static CompiledIdentityKey IdentityKey(CompiledScriptIdentity identity) =>
        new(identity.Sha256.ToUpperInvariant(), identity.Utf16Length, identity.LineCount);

    private static bool SameSha(string? left, string? right) =>
        CommandIdentity.TryNormalizeSha256(left, out string normalizedLeft)
        && CommandIdentity.TryNormalizeSha256(right, out string normalizedRight)
        && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);

    private sealed record PlaybackIndexState(
        CompiledScriptIdentity[] Identities,
        string[] Fingerprints,
        IReadOnlyDictionary<CompiledIdentityKey, List<int>> RecordsByIdentity);

    private sealed class PlaybackCommandIndex : IPlaybackCommandIndex
    {
        private readonly IReadOnlyDictionary<CompiledIdentityKey, List<int>>
            _recordsByIdentity;
        private readonly string[] _fingerprints;
        private readonly IReadOnlyDictionary<int, PlaybackCommandBatch> _batchesByRecord;

        public PlaybackCommandIndex(
            string playbackPathKey,
            string playbackRevisionSha256,
            IReadOnlyDictionary<CompiledIdentityKey, List<int>> recordsByIdentity,
            string[] fingerprints,
            IReadOnlyDictionary<int, PlaybackCommandBatch> batchesByRecord)
        {
            PlaybackPathKey = playbackPathKey;
            PlaybackRevisionSha256 = playbackRevisionSha256;
            _recordsByIdentity = recordsByIdentity;
            _fingerprints = fingerprints;
            _batchesByRecord = batchesByRecord;
        }

        public string PlaybackPathKey { get; }

        public string PlaybackRevisionSha256 { get; }

        public int RecordCount => _fingerprints.Length;

        public int BatchCount => _batchesByRecord.Count;

        public Result<PlaybackCommandObservation> Observe(
            ObservedCompiledSceneIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            Result<CompiledIdentityKey> normalized = NormalizeIdentity(
                new CompiledScriptIdentity(
                    identity.CompiledScriptSha256,
                    identity.CompiledScriptLength,
                    identity.CompiledScriptLineCount),
                "observed scene");
            if (!normalized.Success)
            {
                return Result<PlaybackCommandObservation>.Fail(normalized.Error);
            }

            if (!_recordsByIdentity.TryGetValue(
                    normalized.Value,
                    out List<int>? records)
                || records == null)
            {
                return Result<PlaybackCommandObservation>.Ok(new PlaybackCommandObservation(
                    PlaybackCommandObservationStatus.NotFound,
                    null,
                    null,
                    null,
                    Array.Empty<int>()));
            }

            if (records.Count == 1)
            {
                return Result<PlaybackCommandObservation>.Ok(Describe(records[0], records));
            }

            // Several records share this compiled script (identical dialogue —
            // empty camera-only cards especially). The engine row index observed
            // in the same advance window decides which record is playing; when
            // it is unknown or not one of the candidates the observation stays
            // ambiguous and the runtime keeps failing closed.
            int row = identity.PlaybackRowIndex;
            if (row >= 0 && records.Contains(row))
            {
                return Result<PlaybackCommandObservation>.Ok(Describe(row, records));
            }

            return Result<PlaybackCommandObservation>.Ok(new PlaybackCommandObservation(
                PlaybackCommandObservationStatus.Ambiguous,
                null,
                null,
                null,
                records.AsReadOnly()));
        }

        private PlaybackCommandObservation Describe(int recordIndex, List<int> records) => new(
            _batchesByRecord.TryGetValue(recordIndex, out PlaybackCommandBatch? batch)
                ? PlaybackCommandObservationStatus.CommandBatch
                : PlaybackCommandObservationStatus.OrdinaryRecord,
            recordIndex,
            _fingerprints[recordIndex],
            batch,
            records.AsReadOnly());
    }

    private readonly record struct CompiledIdentityKey(
        string Sha256,
        int Utf16Length,
        int LineCount);
}
