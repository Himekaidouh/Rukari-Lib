using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// What one layer contributes to one dialogue row: how many of the row's directives belong to it, and how long the
/// layer occupies the row (2026-10-13).
///
/// <para>
/// These two numbers are what replaced the standalone timeline panel. The overlay model says a window owns a layer
/// and stacks effects inside it in authored order, so the only question a window has to answer about the row is
/// "what is my layer doing". The ruler, the lanes and the per-segment bars were a second vocabulary for the same
/// fact, and one the author had to learn before the answer could be read at all.
/// </para>
///
/// <para>
/// The row is read the way playback reads it: the extractor accepts several directives for one resource in authored
/// order (the editor's document model does not — it refuses a row that declares two directives for one resource),
/// and <see cref="SlotActionQueuePlanner.PlanDirectives"/> is the same call the player makes before it arms the
/// action hold. A layer whose second directive carries <c>at=</c> therefore reports the offset rather than a sum of
/// durations.
/// </para>
///
/// <para>
/// <see cref="SpanMs"/> is the layer's own extent — its last segment's end minus its first segment's start — and not
/// the moment it finishes on its track. The two differ where two layers really do share one queue: both camera
/// scopes play on the planner's single <c>camera</c> track, so a background camera written after a whole-picture
/// move starts only once that move has run. Reporting the absolute end would tell the background window that its
/// 300 ms effect takes 1100 ms. The line answers "how long is my layer", which is the question a window can answer
/// about itself; when a layer starts is a property of the row, and the row is read in order.
/// </para>
/// </summary>
public sealed record EffectLayerSummary(
    EffectLayer Layer,
    int SegmentCount,
    double SpanMs,
    string Error,
    string Note)
{
    /// <summary>
    /// No row could be read, or the layer cannot be planned. A layer that cannot be read is not empty — it is
    /// unknown — and the caller's reason is shown in place of a stale count.
    /// </summary>
    public static EffectLayerSummary Unavailable(EffectLayer layer, string error) =>
        new(layer ?? throw new ArgumentNullException(nameof(layer)), 0, 0d, error ?? string.Empty, string.Empty);

    /// <summary>Reads one layer out of a row's raw additional-prompt text.</summary>
    public static EffectLayerSummary Read(EffectLayer layer, string? additionalPrompt)
    {
        ArgumentNullException.ThrowIfNull(layer);
        EmbeddedAavtExtraction extraction =
            new EmbeddedAavtDirectiveExtractor().Extract(additionalPrompt ?? string.Empty);
        var directives = new List<EffectLayerDirective>(extraction.Commands.Count);
        foreach (EmbeddedAavtCommand command in extraction.Commands)
        {
            directives.Add(new EffectLayerDirective(
                CommandResourceIdentity.KeyFor(command),
                command.CanonicalDirective));
        }

        EffectLayerSummary summary = FromDirectives(layer, directives);
        int unreadable = extraction.Errors.Count;
        return unreadable == 0 ? summary : summary with { Note = $"原文有 {unreadable} 处无法解析" };
    }

    /// <summary>
    /// The same two numbers for a layer that has not been written yet: the directives a window is about to apply.
    ///
    /// <para>
    /// Planning them on their own gives the layer the count and the extent it would have on the row, because a track's
    /// own timeline depends only on its own directives — which is what lets a window show the stack it is building
    /// before it writes it, instead of showing the row it is about to replace.
    /// </para>
    /// </summary>
    public static EffectLayerSummary FromDirectives(
        EffectLayer layer,
        IReadOnlyList<EffectLayerDirective> directives)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(directives);
        var canonical = new List<string>(directives.Count);
        foreach (EffectLayerDirective directive in directives) canonical.Add(directive.CanonicalDirective);
        Result<SlotActionQueuePlan> planned = SlotActionQueuePlanner.PlanDirectives(canonical);
        if (!planned.Success || planned.Value is null)
            return Unavailable(layer, "无法规划这一行：" + (planned.Error ?? "未知原因"));

        // Segments are matched back to their directive so the layer is decided by the resource a directive occupies
        // rather than by the track it plays on. The planner gives both camera scopes the same track, and gives one
        // character's transform and its preset two different ones; the resource key is the only one of the two that
        // answers "whose layer is this".
        int count = 0;
        double firstStart = 0d;
        double lastEnd = 0d;
        foreach (SlotActionSegment segment in planned.Value.Segments)
        {
            if (segment.DirectiveIndex < 0 || segment.DirectiveIndex >= directives.Count) continue;
            if (!string.Equals(
                    directives[segment.DirectiveIndex].ResourceKey,
                    layer.ResourceKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (count == 0 || segment.StartMs < firstStart) firstStart = segment.StartMs;
            count++;
            if (segment.EndMs > lastEnd) lastEnd = segment.EndMs;
        }

        return new EffectLayerSummary(layer, count, count == 0 ? 0d : lastEnd - firstStart, string.Empty, string.Empty);
    }

    /// <summary>Whether the layer has anything in it. A layer that failed to read is unknown, not empty.</summary>
    public bool HasSegments => Error.Length == 0 && SegmentCount > 0;

    /// <summary>The layer's span in seconds, at the precision the line prints.</summary>
    public string Seconds => (SpanMs / 1000d).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>What the layer is doing, without naming the layer.</summary>
    public string Text
    {
        get
        {
            string text = Error.Length != 0
                ? Error
                : SegmentCount == 0
                    ? "本层暂无效果"
                    : $"本层 {SegmentCount} 段 · 共 {Seconds} s";
            return Note.Length == 0 ? text : text + " · " + Note;
        }
    }

    /// <summary>The one line a window shows.</summary>
    public string Line => Layer.Label + " · " + Text;

    /// <summary>
    /// One line for the log. A hosted window's numbers are otherwise only checkable by someone looking at the
    /// screen, and the owner is part of the claim: a layer reported by the wrong window is a bug this line shows.
    /// </summary>
    public string LogLine => $"[layer] {Layer.ResourceKey} ← {Layer.Window} · {Text}";
}
