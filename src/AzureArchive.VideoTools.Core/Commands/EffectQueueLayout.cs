using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// Where a row's queue marker becomes the directives the player reads (2026-10-13).
///
/// <para>
/// The marker says how a row plays; this turns it into the placement the player already understands, and it is called
/// once per compile at the point the row's extraction enters the caches every later stage reads — the editor preview,
/// the config dispatch path and the project projection all take their directives from that same laid-out extraction.
/// Doing it there rather than in the document is what keeps the layout live: the compile re-reads the row's own text,
/// so editing any layer re-flows the queue by itself and no window can leave another window's timing stale.
/// </para>
///
/// <para>
/// A marker never breaks a row. When the layout cannot be computed — an authored <c>interrupt=</c> is the one case —
/// the row keeps its own directives and the reason is reported, because playing a row exactly as its directives read
/// is a better answer than refusing to play it at all.
/// </para>
/// </summary>
public static class EffectQueueLayout
{
    /// <summary>
    /// The directives a row plays, in order: its own, or the row laid out as a single queue when it carries the
    /// <c>#aavt;queue</c> marker.
    /// </summary>
    public static Result<IReadOnlyList<string>> DirectivesFor(EmbeddedAavtExtraction extracted)
    {
        ArgumentNullException.ThrowIfNull(extracted);
        if (extracted.QueueMode != EffectQueueMode.OneQueue)
        {
            return Result<IReadOnlyList<string>>.Ok(Array.AsReadOnly(
                extracted.Commands.Select(command => command.CanonicalDirective).ToArray()));
        }

        EffectQueuePlan plan = EffectQueueChain.Plan(extracted.Commands);
        return plan.Success
            ? Result<IReadOnlyList<string>>.Ok(plan.Rewritten)
            : Result<IReadOnlyList<string>>.Fail(plan.Error);
    }

    /// <summary>
    /// The extraction the rest of the pipeline should read: the same one, with its commands laid out as one queue when
    /// the row asks for it. <paramref name="error"/> is non-empty only when the row asked and could not be laid out,
    /// in which case the extraction is returned unchanged.
    /// </summary>
    public static EmbeddedAavtExtraction Applied(EmbeddedAavtExtraction extracted, out string error)
    {
        ArgumentNullException.ThrowIfNull(extracted);
        error = string.Empty;
        Result<IReadOnlyList<string>> directives = DirectivesFor(extracted);
        if (!directives.Success || directives.Value is null)
        {
            error = directives.Error ?? "这一行排不出队列。";
            return extracted;
        }

        return directives.Value.Count == extracted.Commands.Count
            ? extracted.WithCanonicalDirectives(directives.Value)
            : extracted;
    }
}
