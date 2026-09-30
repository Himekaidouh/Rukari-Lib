namespace AzureArchive.VideoTools.Core.Playback;

/// <summary>
/// Reporting rule for main-thread stalls (2026-09-19).
/// <para>
/// The user reports the game going unresponsive, concentrated around opening a project, and no
/// log line measured who was holding the thread. A gap between two consecutive managed updates
/// is attribution-free: whatever blocked the main thread — native project loading, the garbage
/// collector, another mod or our own code — shows up as the same number, and the companion
/// question ("did our own previous update take that long?") splits the blame afterwards.
/// </para>
/// <para>
/// Two thresholds, because they answer different questions: every gap at or above
/// <see cref="ReportMilliseconds"/> is felt by the user and is always worth a line, while a gap
/// that merely sets a new session maximum at or above <see cref="NotableMilliseconds"/> is worth
/// exactly one line, so a hitch that never grows into a freeze still becomes visible.
/// </para>
/// </summary>
public static class MainThreadStallPolicy
{
    /// <summary>A gap this long is always reported.</summary>
    public const int ReportMilliseconds = 500;

    /// <summary>A new session maximum this long is reported once, even below the report threshold.</summary>
    public const int NotableMilliseconds = 250;

    public static bool ShouldReport(long gapMilliseconds, long sessionWorstMilliseconds)
    {
        if (gapMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(gapMilliseconds));
        if (sessionWorstMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(sessionWorstMilliseconds));
        if (gapMilliseconds >= ReportMilliseconds)
        {
            return true;
        }

        return gapMilliseconds >= NotableMilliseconds && gapMilliseconds > sessionWorstMilliseconds;
    }

    /// <summary>
    /// Whether our own previous managed update accounts for at least half of the gap. When it
    /// does, the block is inside our update (read <c>previousStage</c> to see where); when it does
    /// not, the main thread was held by something else — or this behaviour was inactive, which
    /// the behaviour logs as its own enable/disable lines.
    /// </summary>
    public static bool IsOurUpdateDominant(long previousUpdateMilliseconds, long gapMilliseconds) =>
        gapMilliseconds > 0
        && previousUpdateMilliseconds > 0
        && previousUpdateMilliseconds * 2 >= gapMilliseconds;
}
