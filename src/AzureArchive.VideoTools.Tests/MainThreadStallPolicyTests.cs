using AzureArchive.VideoTools.Core.Playback;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The rule that turns "the game goes unresponsive" into log lines: a felt freeze is always
/// reported, a new session maximum is reported once, and the blame split is testable.
/// </summary>
internal static class MainThreadStallPolicyTests
{
    public static void FeltFreezesAreAlwaysReported()
    {
        // Reported even when it is nowhere near the session maximum: every freeze is evidence.
        AssertEx.True(MainThreadStallPolicy.ShouldReport(500, 0));
        AssertEx.True(MainThreadStallPolicy.ShouldReport(1834, 5000));
        AssertEx.True(MainThreadStallPolicy.ShouldReport(12000, 60));
    }

    public static void OnlyANewSessionMaximumIsReportedBelowTheThreshold()
    {
        // First sighting of a 300 ms hitch: one line, so a hitch that never freezes is still visible.
        AssertEx.True(MainThreadStallPolicy.ShouldReport(300, 0));
        // The same hitch again: the session already knows about it.
        AssertEx.False(MainThreadStallPolicy.ShouldReport(300, 300));
        // A larger one is news again.
        AssertEx.True(MainThreadStallPolicy.ShouldReport(320, 300));
        // Ordinary frames are never reported.
        AssertEx.False(MainThreadStallPolicy.ShouldReport(249, 0));
        AssertEx.False(MainThreadStallPolicy.ShouldReport(11, 0));
        AssertEx.False(MainThreadStallPolicy.ShouldReport(0, 0));
    }

    public static void BlameSplitsOnHowMuchOfTheGapOurOwnUpdateTook()
    {
        // Our previous update was the whole gap: the block is ours.
        AssertEx.True(MainThreadStallPolicy.IsOurUpdateDominant(900, 1000));
        AssertEx.True(MainThreadStallPolicy.IsOurUpdateDominant(500, 1000));
        // Our update was fast but the gap was seconds: something else held the main thread.
        AssertEx.False(MainThreadStallPolicy.IsOurUpdateDominant(2, 1834));
        AssertEx.False(MainThreadStallPolicy.IsOurUpdateDominant(499, 1000));
        // No measurement yet is not an accusation.
        AssertEx.False(MainThreadStallPolicy.IsOurUpdateDominant(0, 5000));
        AssertEx.False(MainThreadStallPolicy.IsOurUpdateDominant(5, 0));
    }
}
