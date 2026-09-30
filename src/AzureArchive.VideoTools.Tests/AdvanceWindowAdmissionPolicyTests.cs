using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class AdvanceWindowAdmissionPolicyTests
{
    public static void CompletedWindowsKeepTheirExistingAdmission()
    {
        AssertEx.Equal(
            AdvanceWindowAdmission.Standard,
            AdvanceWindowAdmissionPolicy.Classify(
                AdvanceWindowCloseKind.Completed, 0, false, false));
        AssertEx.Equal(
            AdvanceWindowAdmission.Standard,
            AdvanceWindowAdmissionPolicy.Classify(
                AdvanceWindowCloseKind.Completed, 3, false, false));
    }

    public static void SupersededWindowIsAdmissibleOnlyWhenItCapturedIdentity()
    {
        // The measured 22-of-22 failure: the compiled identity lands in the
        // window the engine superseded, never in a completed sibling.
        AssertEx.Equal(
            AdvanceWindowAdmission.SupersededLeaseOnly,
            AdvanceWindowAdmissionPolicy.Classify(
                AdvanceWindowCloseKind.SupersededByOverlappingPrefix, 1, false, false));

        // A superseded window that captured nothing stays rejected, exactly
        // like the stale sibling noise it already is today.
        AssertEx.Equal(
            AdvanceWindowAdmission.Reject,
            AdvanceWindowAdmissionPolicy.Classify(
                AdvanceWindowCloseKind.SupersededByOverlappingPrefix, 0, false, false));

        // A postfix without an open window can never carry a message.
        AssertEx.Equal(
            AdvanceWindowAdmission.Reject,
            AdvanceWindowAdmissionPolicy.Classify(
                AdvanceWindowCloseKind.PrefixWithoutOpen, 0, false, false));
        AssertEx.Equal(
            AdvanceWindowAdmission.Reject,
            AdvanceWindowAdmissionPolicy.Classify(
                AdvanceWindowCloseKind.PrefixWithoutOpen, 2, false, false));
    }

    public static void OverflowAndConflictAlwaysFailClosed()
    {
        AdvanceWindowCloseKind[] kinds =
        {
            AdvanceWindowCloseKind.Completed,
            AdvanceWindowCloseKind.SupersededByOverlappingPrefix,
            AdvanceWindowCloseKind.PrefixWithoutOpen
        };
        foreach (AdvanceWindowCloseKind kind in kinds)
        {
            AssertEx.Equal(
                AdvanceWindowAdmission.Reject,
                AdvanceWindowAdmissionPolicy.Classify(kind, 4, captureOverflowed: true, embeddedCommandConflict: false));
            AssertEx.Equal(
                AdvanceWindowAdmission.Reject,
                AdvanceWindowAdmissionPolicy.Classify(kind, 4, captureOverflowed: false, embeddedCommandConflict: true));
        }
    }

    public static void SameDrainDuplicateSuppressesOnlyOneGenerationOfOneUpdate()
    {
        // Same generation inside the same managed update: the superseded
        // window and the completed sibling must not both dispatch.
        AssertEx.True(AdvanceWindowAdmissionPolicy.IsSameDrainDuplicate(7, 34, 7, 34));

        // Green replay of the same generation arrives in a later update.
        AssertEx.False(AdvanceWindowAdmissionPolicy.IsSameDrainDuplicate(7, 34, 8, 34));

        // A different generation inside one update is its own cascade.
        AssertEx.False(AdvanceWindowAdmissionPolicy.IsSameDrainDuplicate(7, 34, 7, 35));

        // Sentinel: nothing authorized yet must never suppress the first claim.
        AssertEx.False(AdvanceWindowAdmissionPolicy.IsSameDrainDuplicate(-1, -1, 0, 0));
    }
}
