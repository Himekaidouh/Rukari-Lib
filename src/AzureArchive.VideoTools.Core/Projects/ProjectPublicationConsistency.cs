namespace AzureArchive.VideoTools.Core.Projects;

/// <summary>Publish consistency of one AAP/AAS pair on disk.</summary>
public enum ProjectPublicationState
{
    /// <summary>The published playback archive covers the current project
    /// revision, so the pair may be indexed.</summary>
    Consistent = 0,

    /// <summary>The project was written after the playback archive: a save
    /// without a build. Both files exist, but they describe different
    /// revisions.</summary>
    AasStaleAfterSave = 1,

    /// <summary>At least one write time is unusable (file missing or
    /// unreadable), so the ordering cannot be judged.</summary>
    Unknown = 2
}

/// <summary>
/// Save → build consistency of one AAP/AAS pair (2026-09-18).
/// <para>
/// The official autosave writes only the <c>.aap</c>; the <c>.aas</c> is
/// written by the build/publish step (the voice package's guards write it, the
/// official UI never does). A save that has not been built yet therefore leaves
/// the playback archive exactly one revision behind — a state the editor
/// preview cannot see, because the preview never reads disk, but which formal
/// playback and every index built from the pair live in.
/// </para>
/// <para>
/// Measured on 2026-09-18 in the deployed diagnostics log, three consecutive
/// saves produced exactly this ordering and each one failed index load with
/// "Enabled commands at scene … require a unique playback mapping"
/// (<c>sinceAapMs</c> -12237 / -460964 / -7763), recovering only after the
/// following build rewrote the <c>.aas</c>. The ordering is therefore used as
/// the *severity* of that failure — never as a reason to skip a load: a pair
/// whose save happened to keep every scene mappable still indexes normally
/// (observed at 10:56:35, <c>.aap</c> 330 ms newer than the <c>.aas</c>, load
/// READY).
/// </para>
/// <para>
/// A later session (17:14–17:16, 19 loads) settled how weak this signal is on
/// its own: the guard chain writes the <c>.aas</c> about 100–170 ms *before* the
/// autosave writes the <c>.aap</c> in the same cycle, so 14 of 18 detections had
/// <c>.aas</c> older than <c>.aap</c> — by ~90–170 ms, and every one of them
/// loaded READY. The ordering separates a mid-cycle pair from a pair that has
/// been waiting seconds for its build; it does not separate a healthy pair from
/// a broken one. That is why it may only gate a bounded grace window, and why a
/// failure is always reported once that window closes.
/// </para>
/// <para>
/// Only an unambiguous ordering counts. Missing timestamps classify as
/// <see cref="ProjectPublicationState.Unknown"/> so the caller keeps reporting
/// whatever real failure it found instead of masking it as "not built yet".
/// </para>
/// </summary>
public static class ProjectPublicationConsistency
{
    public static ProjectPublicationState Classify(
        DateTime aapWrittenUtc,
        DateTime aasWrittenUtc)
    {
        if (aapWrittenUtc == DateTime.MinValue || aasWrittenUtc == DateTime.MinValue)
        {
            return ProjectPublicationState.Unknown;
        }

        return aasWrittenUtc < aapWrittenUtc
            ? ProjectPublicationState.AasStaleAfterSave
            : ProjectPublicationState.Consistent;
    }

    /// <summary>
    /// How long a save-without-build failure may stay pending before it must be
    /// reported as the failure it is. The compile/publish step was measured to
    /// land 100–400 ms after a save in one session and 1.7–2.5 s after it in
    /// another, on top of a poll that notices the save up to ~0.7 s late; 3.5 s
    /// covers the observed range with margin. Wall clock rather than update
    /// counts: what this bounds is the game's own compile latency, which does
    /// not scale with our frame rate.
    /// <para>
    /// The window is only ever a *delay*, never a mask: it is granted at most
    /// once per successful load, so an archive that keeps failing is reported as
    /// soon as the window closes even if the user keeps saving.
    /// </para>
    /// </summary>
    public const int GraceMilliseconds = 3500;

    /// <summary>
    /// Whether a save-without-build failure may wait for the publish instead of
    /// being reported. <paramref name="graceUntilTicks"/> is the deadline of the
    /// window this load cycle was given, or 0 when it has not been given one
    /// yet; the caller opens it on the first such failure and must never move it
    /// afterwards, so the window cannot be extended by further saves.
    /// </summary>
    public static bool MayWaitForPublish(
        ProjectPublicationState state,
        long graceUntilTicks,
        long nowTicks) =>
        state == ProjectPublicationState.AasStaleAfterSave
        && (graceUntilTicks == 0 || IsInsideGrace(graceUntilTicks, nowTicks));

    public static long GraceDeadlineTicks(long nowTicks) =>
        nowTicks + (GraceMilliseconds * TimeSpan.TicksPerMillisecond);

    public static bool IsInsideGrace(long deadlineTicks, long nowTicks) =>
        deadlineTicks > nowTicks;
}
