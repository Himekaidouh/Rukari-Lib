using AzureArchive.VideoTools.Core.Mapping;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

public sealed record PlaybackAdvanceObservation(
    PlaybackCommandWindowObservationStatus Status,
    int? PlaybackRecordIndex,
    CompiledScriptIdentity? CompiledScript,
    PlaybackCommandBatch? Batch);

/// <summary>
/// Saved playback may consume an overlapping advance only when its captured
/// row and managed compiled identity identify exactly the same saved record.
/// Empty strings are identities; an absent message is not one.
/// </summary>
public static class PlaybackAdvanceDispatchPolicy
{
    /// <summary>
    /// Completed windows retain the existing observer's identity matching,
    /// including a unique nonempty script when the engine row is unavailable.
    /// A genuinely empty compiled script always requires an exact row.
    /// </summary>
    public static Result<PlaybackAdvanceObservation> ObserveCompleted(
        IPlaybackCommandIndex index,
        IReadOnlyList<string> managedMessages,
        int playbackRowIndex)
    {
        Result<PlaybackCommandWindowObservation> observed = new PlaybackCommandWindowObserver().Observe(
            index, managedMessages, playbackRowIndex);
        if (!observed.Success || observed.Value == null)
        {
            return Result<PlaybackAdvanceObservation>.Fail(observed.Error);
        }

        PlaybackCommandWindowObservation match = observed.Value;

        // 2026-10-12 field log: a card whose dialogue is empty advances twice
        // inside one click, so the window that completes carries the identities
        // of the rows the cascade passed over while the row that actually started
        // playing never appears among them. The index then answers MultipleRecords
        // (or AmbiguousIdentity) and the commands bound to that row would never
        // run, even though the window did carry an exact row. The row decides that
        // case, and only that case: the batch has to sit on the named row, so an
        // unknown or commandless row keeps the original fail-closed answer.
        if (match.Status is PlaybackCommandWindowObservationStatus.MultipleRecords
                or PlaybackCommandWindowObservationStatus.AmbiguousIdentity
            && playbackRowIndex >= 0)
        {
            Result<PlaybackAdvanceObservation> byRow = ObserveEmptyFormalRow(index, playbackRowIndex);
            if (byRow.Success
                && byRow.Value is { Status: PlaybackCommandWindowObservationStatus.CommandBatch, Batch: not null })
            {
                return byRow;
            }
        }

        CompiledScriptIdentity? script = match.Batch?.CompiledScript;
        if (script == null && match.PlaybackRecordIndex.HasValue)
        {
            // Ordinary records have no batch to provide their matched identity.
            // Recover only the identity the original observer selected, without
            // treating unrelated Unity diagnostics as authorization.
            foreach (string message in managedMessages)
            {
                CompiledScriptIdentity candidateScript = CommandIdentity.CompiledScript(message);
                Result<PlaybackCommandObservation> candidate = index.Observe(new ObservedCompiledSceneIdentity(
                    candidateScript.Sha256, candidateScript.Utf16Length, candidateScript.LineCount)
                {
                    PlaybackRowIndex = playbackRowIndex
                });
                if (candidate.Success && candidate.Value?.PlaybackRecordIndex == match.PlaybackRecordIndex)
                {
                    script = candidateScript;
                    break;
                }
            }
        }

        if (script?.Utf16Length == 0)
        {
            return ObserveExactRow(index, managedMessages, playbackRowIndex);
        }

        return Result<PlaybackAdvanceObservation>.Ok(new(
            match.Status, match.PlaybackRecordIndex, script, match.Batch));
    }

    /// <summary>
    /// A completed real advance may clean up without an index or identity.
    /// Once row claiming was attempted, only a new successful claim advances
    /// cleanup; a duplicate must not invalidate its already queued sibling.
    /// </summary>
    public static bool ShouldObserveBoundary(bool completedWindow, bool? callChainClaim) =>
        callChainClaim ?? completedWindow;

    public static Result<PlaybackAdvanceObservation> ObserveExactRow(
        IPlaybackCommandIndex index,
        IReadOnlyList<string> managedMessages,
        int playbackRowIndex)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(managedMessages);
        if (playbackRowIndex < 0 || playbackRowIndex >= index.RecordCount)
        {
            return Result<PlaybackAdvanceObservation>.Fail("playback-row-unavailable-or-out-of-range");
        }

        PlaybackCommandObservation? match = null;
        CompiledScriptIdentity? matchedScript = null;
        for (int messageIndex = 0; messageIndex < managedMessages.Count; messageIndex++)
        {
            string? message = managedMessages[messageIndex];
            if (message == null)
            {
                return Result<PlaybackAdvanceObservation>.Fail("playback-managed-message-is-null");
            }

            CompiledScriptIdentity script = CommandIdentity.CompiledScript(message);
            Result<PlaybackCommandObservation> observed = index.Observe(new ObservedCompiledSceneIdentity(
                script.Sha256, script.Utf16Length, script.LineCount)
            {
                PlaybackRowIndex = playbackRowIndex
            });
            if (!observed.Success || observed.Value == null)
            {
                return Result<PlaybackAdvanceObservation>.Fail(observed.Error);
            }

            PlaybackCommandObservation candidate = observed.Value;
            if (candidate.Status == PlaybackCommandObservationStatus.NotFound)
            {
                continue;
            }

            // The existing index intentionally ignores the row for a unique
            // identity. This stricter path must check it even in that case.
            if (candidate.Status == PlaybackCommandObservationStatus.Ambiguous
                || candidate.PlaybackRecordIndex != playbackRowIndex)
            {
                return Result<PlaybackAdvanceObservation>.Fail("playback-row-and-compiled-identity-disagree");
            }

            if (candidate.Status is not (PlaybackCommandObservationStatus.OrdinaryRecord
                    or PlaybackCommandObservationStatus.CommandBatch)
                || (candidate.Status == PlaybackCommandObservationStatus.CommandBatch
                    && (candidate.Batch == null
                        || candidate.Batch.PlaybackRecordIndex != playbackRowIndex
                        || !SameIdentity(candidate.Batch.CompiledScript, script))))
            {
                return Result<PlaybackAdvanceObservation>.Fail("playback-index-result-is-inconsistent");
            }

            if (matchedScript != null && matchedScript != script)
            {
                return Result<PlaybackAdvanceObservation>.Fail("playback-window-captured-conflicting-identities");
            }

            match = candidate;
            matchedScript = script;
        }

        return Result<PlaybackAdvanceObservation>.Ok(new(
            match == null ? PlaybackCommandWindowObservationStatus.NoIdentity
                : match.Status == PlaybackCommandObservationStatus.CommandBatch
                    ? PlaybackCommandWindowObservationStatus.CommandBatch
                    : PlaybackCommandWindowObservationStatus.OrdinaryRecord,
            match?.PlaybackRecordIndex,
            matchedScript,
            match?.Batch));
    }

    /// <summary>
    /// Formal playback fallback for a row whose advance produced no managed
    /// Unity message.  The row is accepted only when the already-bound
    /// projection contains a command batch for that exact record; ordinary and
    /// unknown rows stay <see cref="PlaybackCommandWindowObservationStatus.NoIdentity"/>.
    /// This preserves fail-closed behavior while allowing camera/character
    /// commands on truly empty dialogue rows to run in AAS playback.
    /// </summary>
    public static Result<PlaybackAdvanceObservation> ObserveEmptyFormalRow(
        IPlaybackCommandIndex index,
        int playbackRowIndex)
    {
        ArgumentNullException.ThrowIfNull(index);
        Result<PlaybackCommandObservation> observed = index.ObserveCommandBatchAtRow(
            playbackRowIndex);
        if (!observed.Success || observed.Value == null)
        {
            return Result<PlaybackAdvanceObservation>.Fail(observed.Error);
        }

        PlaybackCommandObservation match = observed.Value;
        if (match.Status != PlaybackCommandObservationStatus.CommandBatch
            || match.Batch == null
            || match.PlaybackRecordIndex != playbackRowIndex
            || match.Batch.PlaybackRecordIndex != playbackRowIndex)
        {
            return Result<PlaybackAdvanceObservation>.Ok(new(
                PlaybackCommandWindowObservationStatus.NoIdentity,
                null,
                null,
                null));
        }

        return Result<PlaybackAdvanceObservation>.Ok(new(
            PlaybackCommandWindowObservationStatus.CommandBatch,
            playbackRowIndex,
            match.Batch.CompiledScript,
            match.Batch));
    }

    private static bool SameIdentity(CompiledScriptIdentity left, CompiledScriptIdentity right) =>
        left.Utf16Length == right.Utf16Length && left.LineCount == right.LineCount
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One admitted call chain may visit several saved rows, but sibling windows
/// for the same row may consume its identity only once. This state survives
/// managed drains; a later call chain may play the same row again.
/// </summary>
public sealed class PlaybackAdvanceDispatchGate
{
    private readonly object _gate = new();
    private readonly Dictionary<int, CompiledScriptIdentity> _claimedRows = new();
    private long _lastCallChainId;
    private long _storageRevision;
    private string _playbackPathKey = string.Empty;
    private string _playbackRevisionSha256 = string.Empty;

    public Result<bool> TryClaim(
        long callChainId,
        long storageRevision,
        IPlaybackCommandIndex index,
        PlaybackAdvanceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(observation);
        if (callChainId <= 0 || storageRevision < 0
            || observation.PlaybackRecordIndex is not int row || row < 0 || row >= index.RecordCount
            || observation.CompiledScript is not { } script
            || !CommandIdentity.TryNormalizeSha256(script.Sha256, out string normalizedScript)
            || script.Utf16Length < 0 || script.LineCount < 1
            || observation.Status is not (PlaybackCommandWindowObservationStatus.CommandBatch
                or PlaybackCommandWindowObservationStatus.OrdinaryRecord)
            || (observation.Status == PlaybackCommandWindowObservationStatus.CommandBatch
                && (observation.Batch == null || observation.Batch.PlaybackRecordIndex != row))
            || !CommandIdentity.TryNormalizeSha256(index.PlaybackPathKey, out string pathKey)
            || !CommandIdentity.TryNormalizeSha256(index.PlaybackRevisionSha256, out string revision))
        {
            return Result<bool>.Fail("playback-call-chain-claim-is-invalid");
        }

        script = script with { Sha256 = normalizedScript };
        lock (_gate)
        {
            if (callChainId < _lastCallChainId)
            {
                return Result<bool>.Fail("playback-call-chain-arrived-out-of-order");
            }

            if (callChainId == _lastCallChainId)
            {
                if (_storageRevision != storageRevision
                    || _playbackPathKey != pathKey || _playbackRevisionSha256 != revision)
                {
                    return Result<bool>.Fail("playback-call-chain-storage-or-index-changed");
                }
            }
            else
            {
                _lastCallChainId = callChainId;
                _storageRevision = storageRevision;
                _playbackPathKey = pathKey;
                _playbackRevisionSha256 = revision;
                _claimedRows.Clear();
            }

            if (_claimedRows.TryGetValue(row, out CompiledScriptIdentity? claimed))
            {
                return claimed == script
                    ? Result<bool>.Ok(false)
                    : Result<bool>.Fail("playback-call-chain-row-identity-changed");
            }

            _claimedRows.Add(row, script);
            return Result<bool>.Ok(true);
        }
    }
}
