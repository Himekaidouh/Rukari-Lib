using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Compilation;

public sealed record PlaybackProjectionObservation(
    int RecordIndex,
    string RecordFingerprint,
    ContinuationPlaybackInstruction? Instruction)
{
    public bool HasInstruction => Instruction != null;
}

public interface IPlaybackProjectionIndex
{
    string PlaybackPathKey { get; }

    string PlaybackRevisionSha256 { get; }

    int RecordCount { get; }

    int InstructionCount { get; }

    Result<PlaybackProjectionObservation> Observe(int recordIndex);
}

public interface IPlaybackProjectionBinder
{
    Result<IPlaybackProjectionIndex> Bind(
        PlaybackArchiveSnapshot playback,
        ContinuationPlaybackProjection? projection);
}

public sealed class PlaybackProjectionBinder : IPlaybackProjectionBinder
{
    public Result<IPlaybackProjectionIndex> Bind(
        PlaybackArchiveSnapshot playback,
        ContinuationPlaybackProjection? projection)
    {
        if (playback?.Source == null || playback.Records == null)
        {
            return Result<IPlaybackProjectionIndex>.Fail(
                "Playback snapshot has no source identity or record collection.");
        }

        if (!IsSha256(playback.Source.PathKey)
            || !IsSha256(playback.Source.RevisionSha256)
            || string.IsNullOrWhiteSpace(playback.SchemaName))
        {
            return Result<IPlaybackProjectionIndex>.Fail(
                "Playback snapshot identity or schema is invalid.");
        }

        var fingerprints = new string[playback.Records.Count];
        for (int index = 0; index < playback.Records.Count; index++)
        {
            PlaybackRecordSnapshot? record = playback.Records[index];
            if (record == null
                || record.RecordIndex != index
                || !IsSha256(record.Fingerprint))
            {
                return Result<IPlaybackProjectionIndex>.Fail(
                    $"Playback record {index} has an invalid index or fingerprint.");
            }

            fingerprints[index] = record.Fingerprint;
        }

        var instructions = new Dictionary<int, ContinuationPlaybackInstruction>();
        if (projection != null)
        {
            Result identity = ValidateProjectionIdentity(playback, projection);
            if (!identity.Success)
            {
                return Result<IPlaybackProjectionIndex>.Fail(identity.Error);
            }

            int previousIndex = -1;
            for (int index = 0; index < projection.Instructions.Count; index++)
            {
                ContinuationPlaybackInstruction? instruction =
                    projection.Instructions[index];
                if (instruction == null)
                {
                    return Result<IPlaybackProjectionIndex>.Fail(
                        $"Projection instruction {index} is null.");
                }

                int recordIndex = instruction.PlaybackRecordIndex;
                if (recordIndex < 0 || recordIndex >= fingerprints.Length)
                {
                    return Result<IPlaybackProjectionIndex>.Fail(
                        $"Projection instruction {index} targets out-of-range playback record {recordIndex}.");
                }

                if (recordIndex <= previousIndex)
                {
                    return Result<IPlaybackProjectionIndex>.Fail(
                        "Projection instructions are not in strictly increasing playback-record order.");
                }

                if (!string.Equals(
                        fingerprints[recordIndex],
                        instruction.PlaybackRecordFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<IPlaybackProjectionIndex>.Fail(
                        $"Projection instruction for playback record {recordIndex} has a stale fingerprint.");
                }

                if (!instructions.TryAdd(recordIndex, instruction))
                {
                    return Result<IPlaybackProjectionIndex>.Fail(
                        $"Projection contains duplicate playback record {recordIndex}.");
                }

                previousIndex = recordIndex;
            }
        }

        IPlaybackProjectionIndex result = new PlaybackProjectionIndex(
            playback.Source.PathKey,
            playback.Source.RevisionSha256,
            fingerprints,
            instructions);
        return Result<IPlaybackProjectionIndex>.Ok(result);
    }

    private static Result ValidateProjectionIdentity(
        PlaybackArchiveSnapshot playback,
        ContinuationPlaybackProjection projection)
    {
        if (projection.SchemaVersion
                != ContinuationPlaybackProjection.CurrentSchemaVersion
            || projection.Instructions == null)
        {
            return Result.Fail(
                "Projection schema or instruction collection is invalid.");
        }

        if (!string.Equals(
                playback.Source.PathKey,
                projection.PlaybackPathKey,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                playback.Source.RevisionSha256,
                projection.PlaybackRevisionSha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                playback.SchemaName,
                projection.PlaybackSchemaName,
                StringComparison.Ordinal))
        {
            return Result.Fail(
                "Projection is not bound to the current playback path, revision, and schema.");
        }

        return Result.Ok();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed class PlaybackProjectionIndex : IPlaybackProjectionIndex
    {
        private readonly string[] _recordFingerprints;
        private readonly IReadOnlyDictionary<int, ContinuationPlaybackInstruction>
            _instructions;

        public PlaybackProjectionIndex(
            string playbackPathKey,
            string playbackRevisionSha256,
            string[] recordFingerprints,
            IReadOnlyDictionary<int, ContinuationPlaybackInstruction> instructions)
        {
            PlaybackPathKey = playbackPathKey;
            PlaybackRevisionSha256 = playbackRevisionSha256;
            _recordFingerprints = recordFingerprints;
            _instructions = instructions;
        }

        public string PlaybackPathKey { get; }

        public string PlaybackRevisionSha256 { get; }

        public int RecordCount => _recordFingerprints.Length;

        public int InstructionCount => _instructions.Count;

        public Result<PlaybackProjectionObservation> Observe(int recordIndex)
        {
            if (recordIndex < 0 || recordIndex >= _recordFingerprints.Length)
            {
                return Result<PlaybackProjectionObservation>.Fail(
                    $"Observed playback record {recordIndex} is outside 0..{_recordFingerprints.Length - 1}.");
            }

            _instructions.TryGetValue(
                recordIndex,
                out ContinuationPlaybackInstruction? instruction);
            return Result<PlaybackProjectionObservation>.Ok(
                new PlaybackProjectionObservation(
                    recordIndex,
                    _recordFingerprints[recordIndex],
                    instruction));
        }
    }
}
