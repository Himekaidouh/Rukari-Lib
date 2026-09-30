using AzureArchive.VideoTools.Core.Projects;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The 2026-09-18 save-without-build measurements, as a classification table.
/// </summary>
internal static class ProjectPublicationConsistencyTests
{
    private static readonly DateTime Aap =
        new(2026, 9, 18, 3, 38, 11, 549, DateTimeKind.Utc);

    public static void AasOlderThanAapIsASaveWithoutABuild()
    {
        // Measured: .aap 03:38:11.549Z against a .aas from 03:30:30.585Z
        // (sinceAapMs -460964), and two more saves 12.2 s and 7.8 s ahead of
        // the published archive.
        AssertEx.Equal(
            ProjectPublicationState.AasStaleAfterSave,
            ProjectPublicationConsistency.Classify(
                Aap,
                Aap.AddMilliseconds(-460964)));
        AssertEx.Equal(
            ProjectPublicationState.AasStaleAfterSave,
            ProjectPublicationConsistency.Classify(Aap, Aap.AddMilliseconds(-12237)));
        AssertEx.Equal(
            ProjectPublicationState.AasStaleAfterSave,
            ProjectPublicationConsistency.Classify(Aap, Aap.AddMilliseconds(-1)));
    }

    public static void AasAtOrAfterAapIsConsistent()
    {
        // Healthy build order: the .aas is published before the .builds
        // directory is stamped, always after the .aap it was built from.
        AssertEx.Equal(
            ProjectPublicationState.Consistent,
            ProjectPublicationConsistency.Classify(Aap, Aap.AddMilliseconds(301)));
        AssertEx.Equal(
            ProjectPublicationState.Consistent,
            ProjectPublicationConsistency.Classify(Aap, Aap));

        // The 10:56:35 case: the .aap was already 330 ms newer and the load
        // still bound, because that save kept every scene mappable. The
        // classification must never be consulted to skip a load.
        AssertEx.Equal(
            ProjectPublicationState.AasStaleAfterSave,
            ProjectPublicationConsistency.Classify(Aap, Aap.AddMilliseconds(-330)));
    }

    public static void MissingWriteTimesStayUnknown()
    {
        AssertEx.Equal(
            ProjectPublicationState.Unknown,
            ProjectPublicationConsistency.Classify(DateTime.MinValue, Aap));
        AssertEx.Equal(
            ProjectPublicationState.Unknown,
            ProjectPublicationConsistency.Classify(Aap, DateTime.MinValue));
        AssertEx.Equal(
            ProjectPublicationState.Unknown,
            ProjectPublicationConsistency.Classify(DateTime.MinValue, DateTime.MinValue));
    }

    public static void GraceIsGrantedOnlyOncePerLoadCycle()
    {
        long now = new DateTime(2026, 9, 18, 9, 15, 0, DateTimeKind.Utc).Ticks;

        // A pair that is behind may be mid-save, and this load cycle has not used
        // its window yet: grant one.
        AssertEx.True(ProjectPublicationConsistency.MayWaitForPublish(
            ProjectPublicationState.AasStaleAfterSave,
            graceUntilTicks: 0,
            now));

        // Inside the window a further failure waits too, but must not move the
        // deadline: the caller passes the ORIGINAL deadline, never a fresh one.
        long deadline = ProjectPublicationConsistency.GraceDeadlineTicks(now);
        AssertEx.True(ProjectPublicationConsistency.MayWaitForPublish(
            ProjectPublicationState.AasStaleAfterSave,
            deadline,
            now + (3400 * TimeSpan.TicksPerMillisecond)));

        // Once the window closes the failure is reported as-is. This is what
        // keeps a churning archive from postponing a real error forever.
        AssertEx.False(ProjectPublicationConsistency.MayWaitForPublish(
            ProjectPublicationState.AasStaleAfterSave,
            deadline,
            deadline));

        // The archive is not behind the project, so no publish is outstanding.
        AssertEx.False(ProjectPublicationConsistency.MayWaitForPublish(
            ProjectPublicationState.Consistent,
            graceUntilTicks: 0,
            now));

        // Nothing could be judged about the ordering: report the real failure.
        AssertEx.False(ProjectPublicationConsistency.MayWaitForPublish(
            ProjectPublicationState.Unknown,
            graceUntilTicks: 0,
            now));
    }

    public static void GraceWindowIsWallClockAndCloses()
    {
        long now = new DateTime(2026, 9, 18, 9, 15, 0, DateTimeKind.Utc).Ticks;
        long deadline = ProjectPublicationConsistency.GraceDeadlineTicks(now);
        AssertEx.Equal(
            now + (ProjectPublicationConsistency.GraceMilliseconds * TimeSpan.TicksPerMillisecond),
            deadline);

        // Must cover the slowest measured publish (2.5 s) plus a late poll (~0.7 s).
        AssertEx.True(ProjectPublicationConsistency.GraceMilliseconds >= 3200);
        AssertEx.True(ProjectPublicationConsistency.IsInsideGrace(deadline, now));
        AssertEx.True(ProjectPublicationConsistency.IsInsideGrace(
            deadline,
            now + (2500 * TimeSpan.TicksPerMillisecond)));

        // The instant the window closes the loader must let an attempt through,
        // so the failure gets reported instead of waiting for another save.
        AssertEx.False(ProjectPublicationConsistency.IsInsideGrace(deadline, deadline));
        AssertEx.False(ProjectPublicationConsistency.IsInsideGrace(
            deadline,
            deadline + TimeSpan.TicksPerMillisecond));

        // A window that was never opened is not "inside" anything: the loader
        // must not treat 0 as a live deadline.
        AssertEx.False(ProjectPublicationConsistency.IsInsideGrace(0, now));
    }
}
