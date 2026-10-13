using System.Globalization;
using AzureArchive.VideoTools.Core.Results;

namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>
/// One effect's place in the queue a row is laid out as (2026-10-13).
///
/// <para>
/// <paramref name="IndexInLayer"/> and <paramref name="LayerKey"/> say which line of the document this effect came
/// from, so a plan can be read back against the row it describes.
/// </para>
/// </summary>
public sealed record EffectQueueStep(
    int RowIndex,
    string LayerKey,
    int IndexInLayer,
    int OffsetMs)
{
    /// <summary>
    /// Whether this effect has to wait for something on another track, which is the only reason a line carries
    /// <c>at=</c>. Effects that follow each other on one track append by themselves and are left alone.
    /// </summary>
    public bool HasOffset => OffsetMs >= 0;
}

/// <summary>
/// What a row looks like once its effects are read as one queue (2026-10-13).
///
/// <para>
/// <see cref="Rewritten"/> is the row's canonical directives in the same order, each carrying the placement the queue
/// needs. Everything here is checked against <see cref="SlotActionQueuePlanner"/>, the same call the player makes
/// before it arms the action hold, so "where each effect starts" is never this file's opinion.
/// </para>
/// </summary>
public sealed record EffectQueuePlan(
    IReadOnlyList<EffectQueueStep> Steps,
    IReadOnlyList<string> Rewritten,
    double TotalMs,
    double BudgetMs,
    bool OverBudget,
    string Note,
    string Error)
{
    /// <summary>Nothing could be planned; the row is left exactly as it was, and the caller says why.</summary>
    public static EffectQueuePlan Failed(string error) => new(
        Array.Empty<EffectQueueStep>(),
        Array.Empty<string>(),
        0d,
        SlotActionQueuePlanner.DefaultBudgetMilliseconds,
        false,
        string.Empty,
        error ?? string.Empty);

    public bool Success => Error.Length == 0;

    /// <summary>The one line a surface shows about the queue a row is in.</summary>
    public string Line
    {
        get
        {
            if (!Success) return Error;
            string head = $"本行串成一条：共 {Seconds(TotalMs)} s";
            return Note.Length == 0 ? head : head + " · " + Note;
        }
    }

    private static string Seconds(double milliseconds) =>
        (milliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture);
}

/// <summary>
/// Lays a whole row out as one queue: 人物-人物-镜头, each effect starting where the one before it ended (2026-10-13).
///
/// <para>
/// The overlay model gives every layer its own track: one character slot is one track, the screen is one track, one
/// slot's presets are one track. Tracks run in parallel and a second effect on a track appends, so a row plays the two
/// character effects one after the other and the camera over the top of both. A row that carries the
/// <c>#aavt;queue</c> marker asks for the other reading, and this turns that into the placement the player already
/// understands: every effect that would otherwise start too early gets <c>at=</c> at the moment the effect before it
/// ends.
/// </para>
///
/// <para>
/// Only the joints carry <c>at=</c>. Two effects that already follow each other on one track need nothing, and writing
/// an offset they do not need would be a second, weaker copy of a fact the track already holds — so the rule is
/// "write it where it changes the outcome, and nowhere else". The result is re-planned and compared against the
/// sequence this file intended, because a row that plays differently from what it says is worse than no queue at all.
/// </para>
///
/// <para>
/// It is called from the embedded compile, which rebuilds the row's projection from the row's own text every time, so
/// nothing here is ever written back into the document and no layout can go stale behind the author's back.
/// </para>
/// </summary>
public static class EffectQueueChain
{
    /// <summary>
    /// Whether a family's grammar carries the queue placement at all. The character transform, the camera and the
    /// transient preset accept <c>at=</c>; the pending start state and the spine overlay do not, and a line that
    /// carried one anyway would be rejected by its own validator.
    /// </summary>
    public static bool SupportsOffset(string canonicalDirective)
    {
        if (string.IsNullOrEmpty(canonicalDirective)) return false;
        return StartsWithFamily(canonicalDirective, "#char;")
            || StartsWithFamily(canonicalDirective, SceneCameraCommandFamilyCompiler.CanonicalRootToken + ";")
            || StartsWithFamily(canonicalDirective, CharacterPresetCommandFamilyCompiler.CanonicalRootToken + ";");
    }

    /// <summary>
    /// The same directive with its offset set to <paramref name="offsetMs"/>. Any offset it already carried is
    /// replaced, so one line never says two things about when it starts.
    /// </summary>
    public static string WithOffset(string directive, int offsetMs)
    {
        ArgumentNullException.ThrowIfNull(directive);
        List<string> parts = WithoutAtToken(directive);
        parts.Add(SlotActionPlacementParser.AtKey + "=" + offsetMs.ToString(CultureInfo.InvariantCulture));
        return string.Join(';', parts);
    }

    /// <summary>The same directive with no offset: the append it gets by default on its own track.</summary>
    public static string WithoutOffset(string directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        return string.Join(';', WithoutAtToken(directive));
    }

    /// <summary>
    /// Lays the row out as one queue.
    /// </summary>
    public static EffectQueuePlan Plan(
        IReadOnlyList<EmbeddedAavtCommand> rowCommands,
        double budgetMilliseconds = SlotActionQueuePlanner.DefaultBudgetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(rowCommands);

        var rewritten = new List<string>(rowCommands.Count);
        var keys = new List<string>(rowCommands.Count);
        var indexInLayer = new List<int>(rowCommands.Count);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (EmbeddedAavtCommand command in rowCommands)
        {
            if (command is null) return EffectQueuePlan.Failed("效果队列收到了一条空指令。");
            string key = CommandResourceIdentity.KeyFor(command);
            ordinals.TryGetValue(key, out int ordinal);
            ordinals[key] = ordinal + 1;
            keys.Add(key);
            indexInLayer.Add(ordinal);
            rewritten.Add(command.CanonicalDirective);
        }

        // One description per directive, from the planner itself: duration and track are its business, and a directive
        // it cannot describe is not a slot action at all rather than an effect with unknown length.
        var inputs = new SlotActionInput?[rewritten.Count];
        var outside = new List<int>();
        for (int index = 0; index < rewritten.Count; index++)
        {
            Result<SlotActionInput> described = SlotActionQueuePlanner.Describe(index, rewritten[index]);
            if (described.Success && described.Value is not null) inputs[index] = described.Value;
            else outside.Add(index);
        }

        var notes = new List<string>();
        if (outside.Count != 0)
        {
            notes.Add(outside.Count == 1
                ? $"第 {outside[0] + 1} 段不在队列里（{keys[outside[0]]}），它和第一段同时开始"
                : $"{outside.Count} 段不在队列里（{keys[outside[0]]} 等），它们和第一段同时开始");
        }

        var steps = new List<EffectQueueStep>(rewritten.Count);

        // interrupt= takes over its own track and cancels what that track still had to run. One shared queue cannot be
        // laid over that without silently rewriting the intent behind it, so the row is refused and the marker's two
        // ways out are named: drop the interrupt=, or drop the marker and let the layers keep their own tracks.
        for (int index = 0; index < inputs.Length; index++)
        {
            if (inputs[index] is { Interrupt: true })
            {
                return EffectQueuePlan.Failed(
                    $"第 {index + 1} 段带 interrupt=，它是自己那条轨的接管指令，和 #aavt;queue 不能共存；"
                    + "去掉 interrupt=，或删掉这一行的队列标记。");
            }
        }

        double chain = 0d;
        var trackCursors = new Dictionary<string, double>(StringComparer.Ordinal);
        bool offsetRewritten = false;
        bool pendingNotQueued = false;
        for (int index = 0; index < rewritten.Count; index++)
        {
            SlotActionInput? input = inputs[index];
            if (input is null)
            {
                steps.Add(new EffectQueueStep(index, keys[index], indexInLayer[index], -1));
                continue;
            }

            trackCursors.TryGetValue(input.SlotKey, out double cursor);
            int desired = (int)Math.Round(chain);
            bool wouldWait = Math.Abs(desired - cursor) >= 0.5d;
            bool canCarry = SupportsOffset(rewritten[index]);
            bool needs = wouldWait && canCarry;
            if (canCarry)
            {
                bool had = input.AtMs >= 0d;
                if (needs)
                {
                    if (!had || (int)Math.Round(input.AtMs) != desired)
                    {
                        // An authored offset this queue changes is reported once: it is the one case where writing the
                        // mode silently rewrites something the author typed by hand.
                        offsetRewritten |= had;
                        rewritten[index] = WithOffset(rewritten[index], desired);
                    }
                }
                else if (had)
                {
                    rewritten[index] = WithoutOffset(rewritten[index]);
                    offsetRewritten = true;
                }
            }
            else if (wouldWait)
            {
                // A family that cannot carry the placement is played where the planner puts it — the pending start
                // state stages a beginning and occupies no time, so it commits in the same frame as everything else.
                // The chain therefore advances by where it really lands, and the effect behind it on that track gets
                // an offset that jumps over the gap.
                pendingNotQueued = true;
            }

            // Where this effect will actually start: its offset when it carries one, and its track's cursor when it
            // does not. Reading it back this way is what keeps the chain equal to the plan the player will build.
            double start = needs ? desired : cursor;
            double end = start + input.DurationMs;
            if (end > chain) chain = end;
            trackCursors[input.SlotKey] = Math.Max(cursor, end);
            steps.Add(new EffectQueueStep(index, keys[index], indexInLayer[index], needs ? desired : -1));
        }

        if (offsetRewritten)
            notes.Add("原有的 at= 已按队列重排");
        if (pendingNotQueued)
            notes.Add("起点预置段（charp）不排队，它仍在同一帧提交");

        return Finish(rewritten, steps, budgetMilliseconds, chain, notes);
    }

    /// <summary>
    /// The player's own answer about the row this queue describes. A layout whose joints do not add up to the sequence
    /// this file intended is a bug in the laying out, not a row to play.
    /// </summary>
    private static EffectQueuePlan Finish(
        IReadOnlyList<string> rewritten,
        IReadOnlyList<EffectQueueStep> steps,
        double budgetMilliseconds,
        double chainMilliseconds,
        List<string> notes)
    {
        Result<SlotActionQueuePlan> planned = SlotActionQueuePlanner.PlanDirectives(rewritten, budgetMilliseconds);
        if (!planned.Success || planned.Value is null)
            return EffectQueuePlan.Failed("无法规划这一行：" + (planned.Error ?? "未知原因"));

        SlotActionQueuePlan plan = planned.Value;
        foreach (string diagnostic in plan.Diagnostics)
        {
            if (diagnostic.StartsWith("overlap", StringComparison.Ordinal))
                return EffectQueuePlan.Failed("队列自检发现两段重叠：" + diagnostic);
        }

        // Over budget is not a failure: the player collapses the tail to its end state on purpose, and that collapse is
        // what the total then reports. Anything else would be the two of us disagreeing.
        double expected = plan.OverBudget ? Math.Min(chainMilliseconds, budgetMilliseconds) : chainMilliseconds;
        if (Math.Abs(plan.TotalMs - expected) > 0.5d)
        {
            return EffectQueuePlan.Failed(
                $"队列自检不一致：播放器算出 {plan.TotalMs:0} ms，队列应长 {expected:0} ms。");
        }

        if (plan.OverBudget)
        {
            notes.Add($"超过 {Seconds(budgetMilliseconds)} s 上限，播放时末尾会直接跳到结束状态");
        }

        return new EffectQueuePlan(
            steps,
            rewritten,
            plan.TotalMs,
            budgetMilliseconds,
            plan.OverBudget,
            string.Join(" · ", notes),
            string.Empty);
    }

    private static bool StartsWithFamily(string directive, string rootToken) =>
        directive.StartsWith(rootToken, StringComparison.Ordinal);

    /// <summary>
    /// The directive's tokens with its <c>at=</c> token removed. Tokens are read the way every family reads them —
    /// split on <c>;</c>, then at the first <c>=</c> — so a value that happens to contain the word cannot be mistaken
    /// for the parameter.
    /// </summary>
    private static List<string> WithoutAtToken(string directive)
    {
        var parts = new List<string>();
        foreach (string token in directive.Split(';'))
        {
            int equals = token.IndexOf('=');
            if (equals > 0
                && string.Equals(
                    token[..equals].Trim(),
                    SlotActionPlacementParser.AtKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts.Add(token);
        }

        return parts;
    }

    private static string Seconds(double milliseconds) =>
        (milliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture);
}
