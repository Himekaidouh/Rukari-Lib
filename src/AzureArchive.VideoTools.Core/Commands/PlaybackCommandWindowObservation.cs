using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public enum PlaybackCommandWindowObservationStatus
{
    NoIdentity = 0,
    AmbiguousIdentity = 1,
    MultipleRecords = 2,
    OrdinaryRecord = 3,
    CommandBatch = 4
}

public sealed record PlaybackCommandWindowObservation(
    PlaybackCommandWindowObservationStatus Status,
    int ExactRecordCount,
    int AmbiguousMessageCount,
    int? PlaybackRecordIndex,
    PlaybackCommandBatch? Batch);

public interface IPlaybackCommandWindowObserver
{
    Result<PlaybackCommandWindowObservation> Observe(
        IPlaybackCommandIndex index,
        IReadOnlyList<string> managedMessages);

    /// <summary>
    /// Row-aware overload (2026-09-18): when several records share the observed
    /// compiled script, the engine row index decides which record is playing.
    /// </summary>
    Result<PlaybackCommandWindowObservation> Observe(
        IPlaybackCommandIndex index,
        IReadOnlyList<string> managedMessages,
        int playbackRowIndex);
}

public sealed class PlaybackCommandWindowObserver : IPlaybackCommandWindowObserver
{
    public Result<PlaybackCommandWindowObservation> Observe(
        IPlaybackCommandIndex index,
        IReadOnlyList<string> managedMessages) =>
        Observe(index, managedMessages, playbackRowIndex: -1);

    public Result<PlaybackCommandWindowObservation> Observe(
        IPlaybackCommandIndex index,
        IReadOnlyList<string> managedMessages,
        int playbackRowIndex)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(managedMessages);

        var records = new Dictionary<int, PlaybackCommandObservation>();
        int ambiguous = 0;
        for (int messageIndex = 0; messageIndex < managedMessages.Count; messageIndex++)
        {
            string? message = managedMessages[messageIndex];
            if (message == null)
            {
                return Result<PlaybackCommandWindowObservation>.Fail(
                    $"Observed managed message {messageIndex} is null.");
            }

            CompiledScriptIdentity identity = CommandIdentity.CompiledScript(message);
            Result<PlaybackCommandObservation> observed = index.Observe(
                new ObservedCompiledSceneIdentity(
                    identity.Sha256,
                    identity.Utf16Length,
                    identity.LineCount)
                {
                    PlaybackRowIndex = playbackRowIndex
                });
            if (!observed.Success || observed.Value == null)
            {
                return Result<PlaybackCommandWindowObservation>.Fail(
                    $"Observed managed message {messageIndex} could not be indexed: {observed.Error}");
            }

            PlaybackCommandObservation value = observed.Value;
            if (value.Status == PlaybackCommandObservationStatus.Ambiguous)
            {
                ambiguous++;
                continue;
            }

            if (value.Status != PlaybackCommandObservationStatus.NotFound
                && value.PlaybackRecordIndex.HasValue)
            {
                records.TryAdd(value.PlaybackRecordIndex.Value, value);
            }
        }

        if (ambiguous != 0)
        {
            return Result<PlaybackCommandWindowObservation>.Ok(new(
                PlaybackCommandWindowObservationStatus.AmbiguousIdentity,
                records.Count,
                ambiguous,
                null,
                null));
        }

        if (records.Count == 0)
        {
            return Result<PlaybackCommandWindowObservation>.Ok(new(
                PlaybackCommandWindowObservationStatus.NoIdentity,
                0,
                0,
                null,
                null));
        }

        PlaybackCommandObservation[] commandRecords = records.Values
            .Where(record => record.Status == PlaybackCommandObservationStatus.CommandBatch
                && record.Batch != null)
            .ToArray();

        if (records.Count != 1)
        {
            if (commandRecords.Length == 1)
            {
                PlaybackCommandObservation commandRecord = commandRecords[0];
                return Result<PlaybackCommandWindowObservation>.Ok(new(
                    PlaybackCommandWindowObservationStatus.CommandBatch,
                    records.Count,
                    0,
                    commandRecord.PlaybackRecordIndex,
                    commandRecord.Batch));
            }

            return Result<PlaybackCommandWindowObservation>.Ok(new(
                PlaybackCommandWindowObservationStatus.MultipleRecords,
                records.Count,
                0,
                null,
                null));
        }

        PlaybackCommandObservation match = records.Values.Single();
        if (commandRecords.Length == 1)
        {
            return Result<PlaybackCommandWindowObservation>.Ok(new(
                PlaybackCommandWindowObservationStatus.CommandBatch,
                1,
                0,
                match.PlaybackRecordIndex,
                match.Batch));
        }

        return Result<PlaybackCommandWindowObservation>.Ok(new(
            PlaybackCommandWindowObservationStatus.OrdinaryRecord,
            1,
            0,
            match.PlaybackRecordIndex,
            null));
    }
}
