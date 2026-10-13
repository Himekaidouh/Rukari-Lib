using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>How one queued segment occupies its slot timeline.</summary>
public enum SlotActionSegmentKind
{
    /// <summary>A finite animation that occupies <c>StartMs..EndMs</c>.</summary>
    Animate = 0,

    /// <summary>A zero-duration commit. It never blocks the segments behind it.</summary>
    Instant = 1,

    /// <summary>An explicit takeover that cancelled the slot's remaining time.</summary>
    Interrupt = 2,

    /// <summary>Cut off by the queue budget; the caller must fast-forward it to its end state.</summary>
    Collapsed = 3
}

/// <summary>
/// One directive as a queue participant: which slot it owns, how long it runs, and
/// the two explicit author overrides. <paramref name="AtMs"/> is a negative value
/// when the author did not ask for an offset.
/// </summary>
public sealed record SlotActionInput(
    int DirectiveIndex,
    string Directive,
    string SlotKey,
    double DurationMs,
    double AtMs = -1d,
    bool Interrupt = false);

/// <summary>One placement of a directive on its slot timeline.</summary>
public sealed record SlotActionSegment(
    string SlotKey,
    int DirectiveIndex,
    string Directive,
    double StartMs,
    double DurationMs,
    SlotActionSegmentKind Kind)
{
    public double EndMs => StartMs + DurationMs;

    /// <summary>
    /// The authored placement that produced this segment, so diagnostics can say
    /// whether <c>at=</c> / <c>interrupt=</c> took effect instead of leaving the
    /// reader to infer it from <see cref="StartMs"/>.
    /// </summary>
    public SlotActionPlacement Placement { get; init; } = SlotActionPlacement.None;
}

/// <summary>
/// The whole row's slot timelines. <see cref="TotalMs"/> is what the action hold
/// must cover; <see cref="OverBudget"/> means at least one segment was collapsed
/// and has to be applied as a final state instead of animated.
/// </summary>
public sealed record SlotActionQueuePlan(
    IReadOnlyList<SlotActionSegment> Segments,
    double TotalMs,
    double BudgetMs,
    bool OverBudget,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Turns one playback row's directives into per-slot timelines.
/// <para>
/// Tracks: one character track per public slot (<c>char:{slot}</c>, shared by
/// <c>#char</c> and the instantaneous <c>#charp</c>), one screen track for every
/// camera directive (<c>camera</c>), and one preset track per public slot
/// (<c>preset:{slot}</c>). Only the preset track may cross the others in time: a
/// preset that starts while the character is still moving keeps its own track, which
/// is the authored keyframe model. Two presets on one slot still append, because the
/// runtime keeps exactly one active preset per slot.
/// </para>
/// <para>
/// The default is APPEND: a second action on the same track runs after the first one
/// ends instead of overwriting its target. Every directive of one row is submitted
/// in the same frame, so the plain dispatcher lets the later write win and the earlier
/// animation is never seen; queueing is what makes the process visible. The rule is
/// the same shape the spine overlay already uses (track + blend) and the only one
/// that can be upgraded to authored keyframes later.
/// </para>
/// <para>
/// Authors can override the placement: <c>at=</c> is an explicit offset from the
/// row's own timeline origin, and <c>interrupt=</c> reproduces the old immediate
/// takeover by cancelling the slot's remaining time. Neither parameter is parsed
/// from directive text yet; callers fill them once the author-facing syntax exists.
/// </para>
/// <para>
/// Two rules are deliberately NOT "drop what does not fit". A relative
/// <c>move;dx=</c> accumulated from a dropped segment leaves an intermediate state
/// the author never wrote, and the next row inherits it. So an over-budget tail is
/// marked <see cref="SlotActionSegmentKind.Collapsed"/> for the caller to
/// fast-forward to its end state, exactly like an engine-forced advance.
/// </para>
/// </summary>
public static class SlotActionQueuePlanner
{
    public const double DefaultBudgetMilliseconds = 3000d;

    /// <summary>Mirrors <see cref="ActionTimelineTiming"/>'s own finite-range bound.</summary>
    public const double MaximumSegmentMilliseconds = 1000d * 1000d;

    /// <summary>
    /// Places already-described inputs. Inputs must arrive in ascending
    /// <see cref="SlotActionInput.DirectiveIndex"/> order so the queue is a pure
    /// function of the authored order.
    /// </summary>
    public static Result<SlotActionQueuePlan> Plan(
        IReadOnlyList<SlotActionInput> inputs,
        double budgetMilliseconds = DefaultBudgetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!double.IsFinite(budgetMilliseconds) || budgetMilliseconds <= 0)
        {
            return Result<SlotActionQueuePlan>.Fail(
                "Slot action queue budget must be a finite positive number of milliseconds.");
        }

        var segments = new List<SlotActionSegment>();
        var cursors = new Dictionary<string, double>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        bool overBudget = false;
        int previousIndex = -1;
        foreach (SlotActionInput input in inputs)
        {
            if (input == null)
            {
                return Result<SlotActionQueuePlan>.Fail("Slot action queue input is null.");
            }

            if (input.DirectiveIndex <= previousIndex)
            {
                return Result<SlotActionQueuePlan>.Fail(
                    "Slot action queue inputs must be in ascending directive order.");
            }

            previousIndex = input.DirectiveIndex;
            if (string.IsNullOrEmpty(input.SlotKey))
            {
                return Result<SlotActionQueuePlan>.Fail(
                    "Slot action queue input has no slot key.");
            }

            if (!double.IsFinite(input.DurationMs)
                || input.DurationMs < 0
                || input.DurationMs > MaximumSegmentMilliseconds)
            {
                return Result<SlotActionQueuePlan>.Fail(
                    $"Slot action queue duration {input.DurationMs} is outside the finite range "
                    + $"for directive {input.DirectiveIndex}.");
            }

            if (input.AtMs >= 0 && !double.IsFinite(input.AtMs))
            {
                return Result<SlotActionQueuePlan>.Fail(
                    $"Slot action queue offset for directive {input.DirectiveIndex} is not finite.");
            }

            cursors.TryGetValue(input.SlotKey, out double cursor);
            double start;
            SlotActionSegmentKind kind;
            if (input.Interrupt)
            {
                // Explicit takeover: cancel this slot's remaining time and start now.
                start = 0;
                int truncated = 0;
                for (int index = 0; index < segments.Count; index++)
                {
                    SlotActionSegment existing = segments[index];
                    if (!string.Equals(existing.SlotKey, input.SlotKey, StringComparison.Ordinal)
                        || existing.EndMs <= start)
                    {
                        continue;
                    }

                    segments[index] = existing with { DurationMs = Math.Max(0, start - existing.StartMs) };
                    truncated++;
                }

                cursor = start;
                if (truncated != 0)
                {
                    diagnostics.Add(
                        $"interrupt; slot={input.SlotKey}; truncated={truncated}; "
                        + $"directive={input.DirectiveIndex}");
                }
            }
            else if (input.AtMs >= 0)
            {
                start = input.AtMs;
                if (start < cursor)
                {
                    diagnostics.Add(
                        $"overlap; slot={input.SlotKey}; at={start}; previousEnd={cursor}; "
                        + $"directive={input.DirectiveIndex}");
                }
            }
            else
            {
                start = cursor;
            }

            double duration = input.DurationMs;
            kind = input.DurationMs <= 0
                ? SlotActionSegmentKind.Instant
                : input.Interrupt
                    ? SlotActionSegmentKind.Interrupt
                    : SlotActionSegmentKind.Animate;
            double requestedEnd = start + duration;
            if (requestedEnd > budgetMilliseconds)
            {
                overBudget = true;
                start = Math.Min(start, budgetMilliseconds);
                duration = Math.Max(0, budgetMilliseconds - start);
                kind = SlotActionSegmentKind.Collapsed;
                diagnostics.Add(
                    $"queue-over-budget; slot={input.SlotKey}; directive={input.DirectiveIndex}; "
                    + $"requestedEndMs={requestedEnd}; budgetMs={budgetMilliseconds}; "
                    + "applyEndStateInsteadOfAnimating=true");
            }

            double end = start + duration;
            segments.Add(new SlotActionSegment(
                input.SlotKey,
                input.DirectiveIndex,
                input.Directive,
                start,
                duration,
                kind)
            {
                Placement = new SlotActionPlacement(
                    input.AtMs >= 0 ? (int)Math.Round(input.AtMs) : -1,
                    input.Interrupt)
            });
            cursors[input.SlotKey] = Math.Max(cursor, end);
        }

        // The row total is read back from the placed segments: an explicit takeover
        // can shorten an earlier segment, so an in-loop accumulator would keep the
        // pre-truncation end and hold the row open for an action that no longer runs.
        double total = 0;
        foreach (SlotActionSegment segment in segments)
        {
            total = Math.Max(total, segment.EndMs);
        }

        return Result<SlotActionQueuePlan>.Ok(new SlotActionQueuePlan(
            segments.AsReadOnly(),
            total,
            budgetMilliseconds,
            overBudget,
            diagnostics.AsReadOnly()));
    }

    /// <summary>
    /// Plans a row's canonical directives. Directives that are not finite slot
    /// actions (the spine overlay and anything unregistered here) stay outside the
    /// queue, exactly as they stay outside the action hold today.
    /// </summary>
    public static Result<SlotActionQueuePlan> PlanDirectives(
        IReadOnlyList<string> canonicalDirectives,
        double budgetMilliseconds = DefaultBudgetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirectives);
        var inputs = new List<SlotActionInput>();
        for (int index = 0; index < canonicalDirectives.Count; index++)
        {
            Result<SlotActionInput> described = Describe(index, canonicalDirectives[index]);
            if (described.Success && described.Value != null)
            {
                inputs.Add(described.Value);
            }
        }

        return Plan(inputs, budgetMilliseconds);
    }

    /// <summary>
    /// Describes one canonical directive as a queue participant. Duration comes from
    /// <see cref="ActionTimelineTiming"/> so the queue and the action hold can never
    /// disagree about how long something takes.
    /// </summary>
    public static Result<SlotActionInput> Describe(int directiveIndex, string canonicalDirective)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirective);
        if (canonicalDirective.StartsWith(
                CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            Result<CharacterPresetCommand> parsed =
                new CharacterPresetDirectiveParser().Parse(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<SlotActionInput>.Fail(parsed.Error);
            }

            // A preset owns its own track, so it may cross the character and screen
            // tracks in time instead of waiting for them, which is the authored
            // keyframe model. Two presets on ONE public slot still share the key and
            // therefore still append: the runtime keeps exactly one active preset per
            // slot (CharacterPresetRuntime._active, its Begin stops the slot first),
            // so parallel presets on one slot are physically impossible and must never
            // be faked by splitting this key by preset kind.
            return Result<SlotActionInput>.Ok(new SlotActionInput(
                directiveIndex,
                canonicalDirective,
                PresetSlotKey(parsed.Value.PublicSlot),
                ActionTimelineTiming.Seconds(parsed.Value) * 1000d,
                OffsetOf(parsed.Value.Placement),
                parsed.Value.Placement.Interrupt));
        }

        if (canonicalDirective.StartsWith(
                SlotPendingCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            Result<CharacterTransformCommand> parsed =
                new SlotPendingCommandFamilyCompiler().ParseCanonical(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<SlotActionInput>.Fail(parsed.Error);
            }

            // A pending slot command only stages a start state; it commits nothing
            // and therefore never occupies time.
            return Result<SlotActionInput>.Ok(new SlotActionInput(
                directiveIndex,
                canonicalDirective,
                CharacterSlotKey(parsed.Value.PublicSlot),
                0d));
        }

        if (canonicalDirective.StartsWith("#char;", StringComparison.Ordinal))
        {
            Result<CharacterTransformCommand> parsed =
                new CharacterTransformDirectiveParser().Parse(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<SlotActionInput>.Fail(parsed.Error);
            }

            return Result<SlotActionInput>.Ok(new SlotActionInput(
                directiveIndex,
                canonicalDirective,
                CharacterSlotKey(parsed.Value.PublicSlot),
                ActionTimelineTiming.Seconds(parsed.Value) * 1000d,
                OffsetOf(parsed.Value.Placement),
                parsed.Value.Placement.Interrupt));
        }

        if (canonicalDirective.StartsWith(
                SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";",
                StringComparison.Ordinal))
        {
            Result<SceneCameraCommand> parsed =
                new SceneCameraDirectiveParser().Parse(canonicalDirective);
            if (!parsed.Success || parsed.Value == null)
            {
                return Result<SlotActionInput>.Fail(parsed.Error);
            }

            return Result<SlotActionInput>.Ok(new SlotActionInput(
                directiveIndex,
                canonicalDirective,
                // The screen is ONE track: both camera scopes compose into the same
                // Back/Spine transforms every frame, so two camera directives must
                // append even when they target different scopes. Splitting this key by
                // Scope used to let them overlap, which made the composite order the
                // thing that decided the picture.
                CameraSlotKey,
                parsed.Value.DurationMilliseconds,
                OffsetOf(parsed.Value.Placement),
                parsed.Value.Placement.Interrupt));
        }

        return Result<SlotActionInput>.Fail(
            "Directive is not a queueable slot action.");
    }

    /// <summary>The character track of one public slot. Pending start states share it.</summary>
    private static string CharacterSlotKey(int publicSlot) => $"char:{publicSlot}";

    /// <summary>Authored offset in milliseconds, or a negative value for a plain append.</summary>
    private static double OffsetOf(SlotActionPlacement placement) =>
        placement.HasOffset ? placement.AtMilliseconds : -1d;

    /// <summary>The preset track of one public slot.</summary>
    private static string PresetSlotKey(int publicSlot) => $"preset:{publicSlot}";

    /// <summary>The single screen track shared by every camera directive.</summary>
    private const string CameraSlotKey = "camera";
}
