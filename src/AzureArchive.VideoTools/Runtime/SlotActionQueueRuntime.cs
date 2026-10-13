using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// One queued segment waiting for its start time on a slot timeline.
/// The absolute deadline is stored, not a frame number: the dispatch schedule
/// type works in frames while a segment duration is milliseconds, and mixing the
/// two would tie an authored duration to the current frame rate.
/// </summary>
public sealed record SlotActionQueuedSegment(
    long Sequence,
    SlotActionSegment Segment,
    double DueAt);

/// <summary>
/// Holds the segments of one playback row that must not be dispatched with the
/// batch itself, keyed by the row's advance window sequence, and hands them back
/// when their start time arrives.
/// <para>
/// Only the second and later actions of a slot are ever queued: a segment whose
/// <c>StartMs</c> is zero keeps today's same-frame dispatch, so a row with a single
/// action behaves exactly as before. This type deliberately references no plugin,
/// log or interop type, so the test project can link this single file and cover the
/// dispatch timing offline.
/// </para>
/// <para>
/// The key is the window sequence rather than the engine row because one advance
/// window carries exactly one row's batch, and the sequence is what both the
/// enqueue point and the action hold's release path already have in hand.
/// </para>
/// </summary>
public sealed class SlotActionQueueRuntime
{
    private readonly object _gate = new();
    private readonly List<SlotActionQueuedSegment> _pending = new();
    private readonly Dictionary<long, double> _origins = new();

    /// <summary>Segments still waiting for their start time across every row.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count == 0;
            }
        }
    }

    /// <summary>
    /// Registers the not-yet-due segments of one row. <paramref name="now"/> is the
    /// row's timeline origin (same clock as <see cref="ActionTimelineCompletionGate"/>).
    /// A repeated enqueue for the same sequence replaces its previous queue, because
    /// the plan is a pure function of that row's batch.
    /// </summary>
    public void Enqueue(long sequence, SlotActionQueuePlan plan, double now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!double.IsFinite(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        lock (_gate)
        {
            _pending.RemoveAll(item => item.Sequence == sequence);
            _origins[sequence] = now;
            foreach (SlotActionSegment segment in plan.Segments)
            {
                if (segment.StartMs <= 0 || !(segment.DurationMs > 0))
                {
                    // Zero-delay and instantaneous segments are dispatched with the
                    // batch itself; queueing them would only add a frame of latency.
                    continue;
                }

                _pending.Add(new SlotActionQueuedSegment(
                    sequence, segment, now + (segment.StartMs / 1000d)));
            }
        }
    }

    /// <summary>
    /// Removes and returns every queued segment whose start time has arrived, in
    /// row and directive order. Segments are never returned twice.
    /// </summary>
    public IReadOnlyList<SlotActionQueuedSegment> TakeDue(double now)
    {
        if (!double.IsFinite(now))
        {
            return Array.Empty<SlotActionQueuedSegment>();
        }

        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return Array.Empty<SlotActionQueuedSegment>();
            }

            var due = new List<SlotActionQueuedSegment>();
            for (int index = _pending.Count - 1; index >= 0; index--)
            {
                if (_pending[index].DueAt <= now)
                {
                    due.Add(_pending[index]);
                    _pending.RemoveAt(index);
                }
            }

            if (due.Count == 0)
            {
                return Array.Empty<SlotActionQueuedSegment>();
            }

            due.Reverse();
            due.Sort(static (left, right) =>
            {
                int bySequence = left.Sequence.CompareTo(right.Sequence);
                return bySequence != 0
                    ? bySequence
                    : left.Segment.DirectiveIndex.CompareTo(right.Segment.DirectiveIndex);
            });
            return due.AsReadOnly();
        }
    }

    /// <summary>
    /// Removes and returns one row's still-queued segments, in directive order.
    /// The caller applies each segment's end state immediately: a forced advance
    /// fast-forwards the remaining time instead of dropping it, because a relative
    /// <c>dx</c> accumulated from a dropped segment leaves an intermediate state the
    /// author never wrote and the next row inherits it.
    /// </summary>
    public IReadOnlyList<SlotActionQueuedSegment> FastForwardRemaining(long sequence)
    {
        lock (_gate)
        {
            var remaining = new List<SlotActionQueuedSegment>();
            for (int index = _pending.Count - 1; index >= 0; index--)
            {
                if (_pending[index].Sequence == sequence)
                {
                    remaining.Add(_pending[index]);
                    _pending.RemoveAt(index);
                }
            }

            _origins.Remove(sequence);
            if (remaining.Count == 0)
            {
                return Array.Empty<SlotActionQueuedSegment>();
            }

            remaining.Reverse();
            return remaining.AsReadOnly();
        }
    }

    /// <summary>Drops one row's queue, e.g. after it was fast-forwarded or left.</summary>
    public void Clear(long sequence)
    {
        lock (_gate)
        {
            _pending.RemoveAll(item => item.Sequence == sequence);
            _origins.Remove(sequence);
        }
    }

    /// <summary>
    /// Whether one row still owes a segment. The caller uses this to release the
    /// dispatch context it retained for that row: a row whose action hold never arms
    /// (the timeline is disabled) still drains through the managed tick, so its
    /// context must not survive the last segment.
    /// </summary>
    public bool HasPending(long sequence)
    {
        lock (_gate)
        {
            return _pending.Any(item => item.Sequence == sequence);
        }
    }

    /// <summary>Drops everything, e.g. when the index is reloaded or playback stops.</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _pending.Clear();
            _origins.Clear();
        }
    }

    /// <summary>Milliseconds elapsed on one row's timeline, or -1 when it is not queued.</summary>
    public double ElapsedMilliseconds(long sequence, double now)
    {
        lock (_gate)
        {
            return _origins.TryGetValue(sequence, out double origin) && double.IsFinite(now)
                ? (now - origin) * 1000d
                : -1d;
        }
    }
}

/// <summary>
/// Which of one row's authored commands land in this frame and which wait for their
/// slot timeline. <see cref="Plan"/> is null when the row needs no queue at all
/// (fewer than two commands) or when planning failed — in both cases the caller
/// keeps today's same-frame behaviour.
/// </summary>
public sealed record SlotActionDeferralDecision(
    SlotActionQueuePlan? Plan,
    string Error,
    IReadOnlyList<int> SameFrameIndices,
    IReadOnlySet<int> DeferredIndices)
{
    public bool HasDeferredSegments => DeferredIndices.Count != 0;
}

/// <summary>
/// The single deferral decision both dispatch paths take. The playback batch path and
/// the editor preview path call this with their own command list, so preview and
/// playback can never disagree about which action starts when — which is the whole
/// point of the queue: an author must see in the editor exactly what playback does.
/// </summary>
public static class SlotActionQueueDeferral
{
    public static SlotActionDeferralDecision Decide(IReadOnlyList<string> canonicalDirectives)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirectives);
        var all = new List<int>(canonicalDirectives.Count);
        for (int index = 0; index < canonicalDirectives.Count; index++)
        {
            all.Add(index);
        }

        var none = new HashSet<int>();
        if (canonicalDirectives.Count < 2)
        {
            return new SlotActionDeferralDecision(null, string.Empty, all.AsReadOnly(), none);
        }

        Result<SlotActionQueuePlan> planned =
            SlotActionQueuePlanner.PlanDirectives(canonicalDirectives);
        if (!planned.Success || planned.Value == null)
        {
            return new SlotActionDeferralDecision(null, planned.Error, all.AsReadOnly(), none);
        }

        SlotActionQueuePlan plan = planned.Value;
        var deferred = new HashSet<int>();
        foreach (SlotActionSegment segment in plan.Segments)
        {
            if (segment.StartMs > 0 && segment.DurationMs > 0)
            {
                deferred.Add(segment.DirectiveIndex);
            }
        }

        if (deferred.Count == 0)
        {
            return new SlotActionDeferralDecision(plan, string.Empty, all.AsReadOnly(), deferred);
        }

        var sameFrame = new List<int>(canonicalDirectives.Count - deferred.Count);
        foreach (int index in all)
        {
            if (!deferred.Contains(index))
            {
                sameFrame.Add(index);
            }
        }

        return new SlotActionDeferralDecision(
            plan, string.Empty, sameFrame.AsReadOnly(), deferred);
    }
}
