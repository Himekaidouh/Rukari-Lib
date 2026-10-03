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
    PostfixWithoutPrefix = 2
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
    int PlaybackRowReadBefore);

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
    {
        lock (Gate)
        {
            if (_active != null)
            {
                Enqueue(CloseActive(
                    AdvanceWindowCloseReason.ReplacedByOverlappingPrefix,
                    playbackRowIndex));
            }

            long sequence = Interlocked.Increment(ref _sequence);
            _active = new ActiveWindow(sequence) { PlaybackRowIndex = playbackRowIndex };
            return sequence;
        }
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
            Array.AsReadOnly(active.Messages.ToArray()),
            active.RejectedMessageCount,
            active.CaptureOverflowed,
            active.EmbeddedCommandBatch,
            active.EmbeddedCommandConflict,
            playbackRowIndex >= 0 ? playbackRowIndex : active.PlaybackRowIndex,
            active.PlaybackRowIndex);
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

        public List<string> Messages { get; } = new();

        public int CapturedCharacters { get; set; }

        /// <summary>Row index read before this window's advance ran.</summary>
        public int PlaybackRowIndex { get; set; } = -1;

        public int RejectedMessageCount { get; set; }

        public bool CaptureOverflowed { get; set; }

        public PlaybackCommandBatch? EmbeddedCommandBatch { get; set; }

        public bool EmbeddedCommandConflict { get; set; }
    }

    private static bool SameEmbeddedBatch(
        PlaybackCommandBatch left,
        PlaybackCommandBatch right) =>
        left.PlaybackRecordIndex == right.PlaybackRecordIndex
        && left.CompiledScript == right.CompiledScript
        && left.Commands.Select(command => command.CanonicalDirective)
            .SequenceEqual(right.Commands.Select(command => command.CanonicalDirective), StringComparer.Ordinal);
}
