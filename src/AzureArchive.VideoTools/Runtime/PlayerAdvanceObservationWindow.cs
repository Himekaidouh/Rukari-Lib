using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Runtime;

internal enum AdvanceWindowCloseReason
{
    Completed = 0,
    ReplacedByOverlappingPrefix = 1,
    PostfixWithoutPrefix = 2,
    Aborted = 3
}

internal sealed class AdvanceCallChainState
{
    private int _aborted;
    internal bool Aborted => Volatile.Read(ref _aborted) != 0;
    internal void Abort() => Interlocked.Exchange(ref _aborted, 1);
}

internal sealed record ClosedAdvanceWindow(
    long Sequence,
    AdvanceWindowCloseReason CloseReason,
    IReadOnlyList<string> ManagedUnityMessages,
    int RejectedMessageCount,
    bool CaptureOverflowed,
    PlaybackCommandBatch? EmbeddedCommandBatch,
    bool EmbeddedCommandConflict,
    int PlaybackRowIndex,
    int PlaybackRowReadBefore)
{
    internal long StorageRevision { get; init; }
    internal long CallChainId { get; init; }
    internal AdvanceCallChainState? CallChainState { get; init; }

    /// <summary>
    /// Which capture rule refused the second parsed input: "text" (a different
    /// compiled script) or "row" (a different playback row). Empty when the
    /// conflict came from the batch attach path or the branch is unknown, so an
    /// unknown conflict still reports the original reason string unchanged.
    /// </summary>
    internal string EmbeddedCommandConflictBranch { get; init; } = string.Empty;

    /// <summary>
    /// Field list naming both identities involved in that conflict. Built by the
    /// window (which may not do I/O: this file is linked standalone into the test
    /// host) and written to the operator log by the runtime, which is the only
    /// layer that owns the log. Empty when there is nothing to report.
    /// </summary>
    internal string EmbeddedCommandConflictDetail { get; init; } = string.Empty;

    internal bool BelongsToStorage(long revision) => StorageRevision == revision;
}

internal readonly record struct AdvanceWindowLifecycleSnapshot(
    long ActiveWindowSequence,
    long IssuedWindowSequence,
    long ClosedWindowWatermark);

internal static class PlayerAdvanceObservationWindow
{
    private const int MaximumMessages = 64;
    private const int MaximumCharacters = 512 * 1024;
    private const int MaximumQueuedWindows = 512;
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<ClosedAdvanceWindow> Closed = new();
    private static ActiveWindow? _active;
    private static readonly List<long> ManagedCalls = new();
    private static long _managedCallChainId;
    private static AdvanceCallChainState? _managedCallChainState;
    private static long _sequence;
    private static long _closedSequenceWatermark;
    private static int _queuedWindows;
    private static int _droppedWindows;

    public static long IssuedSequence => Interlocked.Read(ref _sequence);

    /// <summary>
    /// Highest window sequence whose close has been recorded. Editor preview
    /// generations use this as their eligibility floor: DataList fires in the
    /// middle of a preview cascade, AFTER the click's windows already opened,
    /// so "issued" overcounts them while "closed" correctly contains only
    /// windows from earlier interactions.
    /// </summary>
    public static long ClosedSequenceWatermark =>
        Interlocked.Read(ref _closedSequenceWatermark);

    internal static AdvanceWindowLifecycleSnapshot ReadLifecycleSnapshot()
    {
        lock (Gate)
        {
            return new AdvanceWindowLifecycleSnapshot(
                _active?.Sequence ?? 0,
                _sequence,
                _closedSequenceWatermark);
        }
    }

    /// <summary>
    /// Opens one window. <paramref name="playbackRowIndex"/> is the engine row
    /// index (<c>Test.cur</c>) read BEFORE this advance runs — i.e. the row of
    /// the card that is currently displayed — or -1 when unknown. It is handed
    /// to a window this call supersedes, because that window played exactly the
    /// card the row still points at.
    /// </summary>
    public static long Open(int playbackRowIndex = -1)
        => Open(playbackRowIndex, 0);

    internal static long Open(int playbackRowIndex, long storageRevision)
    {
        lock (Gate)
        {
            return OpenCore(playbackRowIndex, storageRevision, 0);
        }
    }

    /// <summary>
    /// Begins an admitted managed prefix. Only scalar Harmony state is retained;
    /// nested advances share the outer call's identity even when one nested
    /// postfix has already closed the active capture before another begins.
    /// </summary>
    internal static long BeginAdvance(int playbackRowIndex, long storageRevision)
    {
        lock (Gate)
        {
            if (ManagedCalls.Count == 0) _managedCallChainState = new AdvanceCallChainState();
            long sequence = OpenCore(playbackRowIndex, storageRevision,
                ManagedCalls.Count == 0 ? 0 : _managedCallChainId);
            _active!.CallChainState = _managedCallChainState;
            if (ManagedCalls.Count == 0) _managedCallChainId = sequence;
            ManagedCalls.Add(sequence);
            return sequence;
        }
    }

    internal static void EndAdvance(long sequence, int playbackRowIndex, bool originalRan = true)
    {
        if (!originalRan)
        {
            AbortAdvance(sequence);
            return;
        }
        if (sequence <= 0) return;
        lock (Gate)
        {
            int callIndex = ManagedCalls.LastIndexOf(sequence);
            if (callIndex < 0) return;
            if (callIndex != ManagedCalls.Count - 1)
            {
                // A malformed callback order must not close another call as a
                // successful boundary or leave its chain attached to a replay.
                AbortAdvanceCore(callIndex);
                return;
            }

            if (_active?.Sequence == sequence)
            {
                Enqueue(CloseActive(AdvanceWindowCloseReason.Completed, playbackRowIndex));
            }

            ManagedCalls.RemoveAt(callIndex);
            if (ManagedCalls.Count == 0)
            {
                _managedCallChainId = 0;
                _managedCallChainState = null;
            }
        }
    }

    internal static void AbortAdvance(long sequence)
    {
        if (sequence <= 0) return;
        lock (Gate)
        {
            int callIndex = ManagedCalls.LastIndexOf(sequence);
            if (callIndex >= 0)
            {
                AbortAdvanceCore(callIndex);
                return;
            }

            // A later plugin postfix can throw after our successful postfix
            // already removed its managed call. Retained queued captures still
            // provide the chain marker; consumed windows need no history cache.
            foreach (ClosedAdvanceWindow pending in Closed)
            {
                if (pending.Sequence != sequence) continue;
                pending.CallChainState?.Abort();
                break;
            }
        }
    }

    private static void AbortAdvanceCore(int callIndex)
    {
        // Closed siblings retain this managed marker. Dequeue rejects those
        // still awaiting observation; records already consumed remain unchanged.
        _managedCallChainState?.Abort();
        if (_active != null && ManagedCalls.Contains(_active.Sequence))
        {
            Enqueue(CloseActive(AdvanceWindowCloseReason.Aborted, -1));
        }

        ManagedCalls.RemoveRange(callIndex, ManagedCalls.Count - callIndex);
        if (ManagedCalls.Count == 0)
        {
            _managedCallChainId = 0;
            _managedCallChainState = null;
        }
    }

    private static long OpenCore(int playbackRowIndex, long storageRevision, long callChainId)
    {
        if (_active != null)
        {
            Enqueue(CloseActive(
                AdvanceWindowCloseReason.ReplacedByOverlappingPrefix,
                playbackRowIndex));
        }

        long sequence = Interlocked.Increment(ref _sequence);
        _active = new ActiveWindow(sequence)
        {
            PlaybackRowIndex = playbackRowIndex,
            StorageRevision = storageRevision,
            CallChainId = callChainId > 0 ? callChainId : sequence
        };
        return sequence;
    }

    public static void Capture(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (Gate)
        {
            ActiveWindow? active = _active;
            if (active == null)
            {
                return;
            }

            if (active.Messages.Count >= MaximumMessages
                || text.Length > MaximumCharacters - active.CapturedCharacters)
            {
                active.RejectedMessageCount++;
                active.CaptureOverflowed = true;
                return;
            }

            active.Messages.Add(text);
            active.CapturedCharacters += text.Length;
        }
    }

    // The parser's actual input is authoritative. Debug messages are retained as a legacy
    // fallback only; duplicate logging must not make one parsed card look like two cards.
    internal static long CaptureParsedScript(string text, int playbackRowIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (Gate)
        {
            ActiveWindow? active = _active;
            if (active == null) return 0;
            if (text.Length > MaximumCharacters) { active.CaptureOverflowed = true; return 0; }
            if (active.ParsedScript != null && !string.Equals(active.ParsedScript, text, StringComparison.Ordinal))
            {
                DescribeParsedScriptConflict(active, "text", text, playbackRowIndex);
                active.EmbeddedCommandConflict = true;
                active.EmbeddedCommandConflictBranch = "text";
                return 0;
            }

            if (active.ParsedRowIndex >= 0 && playbackRowIndex >= 0
                && active.ParsedRowIndex != playbackRowIndex)
            {
                DescribeParsedScriptConflict(active, "row", text, playbackRowIndex);
                active.EmbeddedCommandConflict = true;
                active.EmbeddedCommandConflictBranch = "row";
                return 0;
            }

            active.ParsedScript = text;
            if (playbackRowIndex >= 0) active.ParsedRowIndex = playbackRowIndex;
            return active.Sequence;
        }
    }

    /// <summary>
    /// Fills in the conflict detail for the FIRST refusal of a window: this method runs
    /// on every ParseScript, so a repeated refusal inside the same window must not
    /// rewrite (or later flood) the diagnostic. No I/O happens here — the runtime writes
    /// the field list to the operator log together with the rejection it already emits.
    /// </summary>
    private static void DescribeParsedScriptConflict(
        ActiveWindow active,
        string branch,
        string incomingText,
        int incomingRow)
    {
        if (active.EmbeddedCommandConflict)
        {
            return;
        }

        string captured = active.ParsedScript ?? string.Empty;
        active.EmbeddedCommandConflictDetail =
            $"branch={branch}; "
            + $"capturedSha16={CommandIdentity.CompiledScript(captured).Sha256[..16]}; "
            + $"capturedLength={captured.Length}; capturedRow={active.ParsedRowIndex}; "
            + $"rejectedSha16={CommandIdentity.CompiledScript(incomingText).Sha256[..16]}; "
            + $"rejectedLength={incomingText.Length}; rejectedRow={incomingRow}";
    }

    public static bool AttachEmbeddedCommandBatch(PlaybackCommandBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        lock (Gate)
        {
            ActiveWindow? active = _active;
            if (active == null)
            {
                return false;
            }

            if (active.EmbeddedCommandBatch == null)
            {
                active.EmbeddedCommandBatch = batch;
                return true;
            }

            if (!SameEmbeddedBatch(active.EmbeddedCommandBatch, batch))
            {
                active.EmbeddedCommandConflict = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Closes the active window. <paramref name="playbackRowIndex"/> is the
    /// engine row index read AFTER this advance ran — i.e. the card that was
    /// just played — or -1 when unknown; the row captured at open is the
    /// fallback.
    /// </summary>
    public static void Close(int playbackRowIndex = -1)
    {
        lock (Gate)
        {
            if (_active == null)
            {
                Enqueue(new ClosedAdvanceWindow(
                    Interlocked.Increment(ref _sequence),
                    AdvanceWindowCloseReason.PostfixWithoutPrefix,
                    Array.Empty<string>(),
                    0,
                    false,
                    null,
                    false,
                    playbackRowIndex,
                    -1));
                return;
            }

            Enqueue(CloseActive(AdvanceWindowCloseReason.Completed, playbackRowIndex));
        }
    }

    public static bool TryDequeue(out ClosedAdvanceWindow? window)
    {
        if (!Closed.TryDequeue(out window))
        {
            return false;
        }

        Interlocked.Decrement(ref _queuedWindows);
        if (window.CallChainState?.Aborted == true)
        {
            window = window with { CloseReason = AdvanceWindowCloseReason.Aborted };
        }
        return true;
    }

    public static int TakeDroppedWindowCount() =>
        Interlocked.Exchange(ref _droppedWindows, 0);

    private static ClosedAdvanceWindow CloseActive(
        AdvanceWindowCloseReason reason,
        int playbackRowIndex)
    {
        ActiveWindow active = _active!;
        _active = null;
        return new ClosedAdvanceWindow(
            active.Sequence,
            reason,
            Array.AsReadOnly(active.ParsedScript is null ? active.Messages.ToArray() : new[] { active.ParsedScript }),
            active.RejectedMessageCount,
            active.CaptureOverflowed,
            active.EmbeddedCommandBatch,
            active.EmbeddedCommandConflict,
            active.ParsedRowIndex >= 0 ? active.ParsedRowIndex
                : playbackRowIndex >= 0 ? playbackRowIndex : active.PlaybackRowIndex,
            active.PlaybackRowIndex)
        {
            StorageRevision = active.StorageRevision,
            CallChainId = active.CallChainId,
            CallChainState = active.CallChainState,
            EmbeddedCommandConflictBranch = active.EmbeddedCommandConflictBranch,
            EmbeddedCommandConflictDetail = active.EmbeddedCommandConflictDetail
        };
    }

    private static void Enqueue(ClosedAdvanceWindow window)
    {
        // Closing a window advances its lifecycle even when the bounded
        // observation queue cannot retain its record. Otherwise a later log
        // can mistake a dropped, already-closed window for an open one.
        // Always invoked under Gate; the watermark only ever moves forward.
        if (window.Sequence > _closedSequenceWatermark)
        {
            _closedSequenceWatermark = window.Sequence;
        }

        int queued = Interlocked.Increment(ref _queuedWindows);
        if (queued > MaximumQueuedWindows)
        {
            Interlocked.Decrement(ref _queuedWindows);
            Interlocked.Increment(ref _droppedWindows);
            return;
        }

        Closed.Enqueue(window);
    }

    private sealed class ActiveWindow
    {
        public ActiveWindow(long sequence)
        {
            Sequence = sequence;
        }

        public long Sequence { get; }
        public long StorageRevision { get; init; }
        public long CallChainId { get; init; }
        public AdvanceCallChainState? CallChainState { get; set; }

        public List<string> Messages { get; } = new();
        public string? ParsedScript { get; set; }
        public int ParsedRowIndex { get; set; } = -1;

        public int CapturedCharacters { get; set; }

        /// <summary>Row index read before this window's advance ran.</summary>
        public int PlaybackRowIndex { get; set; } = -1;

        public int RejectedMessageCount { get; set; }

        public bool CaptureOverflowed { get; set; }

        public PlaybackCommandBatch? EmbeddedCommandBatch { get; set; }

        public bool EmbeddedCommandConflict { get; set; }

        /// <summary>"text" or "row" for the first conflict this window saw.</summary>
        public string EmbeddedCommandConflictBranch { get; set; } = string.Empty;

        /// <summary>Field list for that conflict; built once, on the first refusal.</summary>
        public string EmbeddedCommandConflictDetail { get; set; } = string.Empty;
    }

    private static bool SameEmbeddedBatch(
        PlaybackCommandBatch left,
        PlaybackCommandBatch right) =>
        left.PlaybackRecordIndex == right.PlaybackRecordIndex
        && left.CompiledScript == right.CompiledScript
        && left.Commands.Select(command => command.CanonicalDirective)
            .SequenceEqual(right.Commands.Select(command => command.CanonicalDirective), StringComparer.Ordinal);
}
