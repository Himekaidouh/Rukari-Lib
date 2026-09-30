namespace AzureArchive.VideoTools.Core.Commands;

/// <summary>Close shape of one observed player advance window.</summary>
public enum AdvanceWindowCloseKind
{
    /// <summary>The prefix/postfix pair completed normally.</summary>
    Completed = 0,

    /// <summary>A new prefix arrived while this window was still open, so the
    /// engine advanced again inside one click cascade.</summary>
    SupersededByOverlappingPrefix = 1,

    /// <summary>A postfix arrived with no open window, so nothing was captured.</summary>
    PrefixWithoutOpen = 2
}

/// <summary>How an observed advance window may be consumed.</summary>
public enum AdvanceWindowAdmission
{
    /// <summary>Behaves exactly like a completed window.</summary>
    Standard = 0,

    /// <summary>Superseded, but it captured the compiled-script identity. Only
    /// the authoritative editor lease may consume it, and only once per drain.</summary>
    SupersededLeaseOnly = 1,

    /// <summary>Not admissible at all; fail closed.</summary>
    Reject = 2
}

/// <summary>
/// Admission rule for one observed advance window (2026-09-18).
/// <para>
/// A window the engine superseded with an overlapping prefix still captured the
/// managed "compiled script" message. When a card's dialogue is empty or only a
/// few characters long, the engine advances several times inside ONE click
/// cascade: the identity lands in exactly those superseded windows while every
/// completed window of the same cascade stays empty. Discarding them made the
/// whole generation unclaimable — measured on 2026-09-18, 22 of 22 generations
/// of one three-character card never dispatched anything, while the same build
/// claimed 6 of 6 ordinary cards and 8 of 8 continue markers.
/// </para>
/// <para>
/// The relaxation is deliberately narrow: a superseded window is admissible
/// ONLY when it actually captured at least one message, and then only for the
/// authoritative editor lease. The cached, embedded-batch and chain-only
/// fallbacks keep requiring a completed window, so one cascade can never
/// dispatch twice through them.
/// </para>
/// </summary>
public static class AdvanceWindowAdmissionPolicy
{
    public static AdvanceWindowAdmission Classify(
        AdvanceWindowCloseKind closeKind,
        int managedMessageCount,
        bool captureOverflowed,
        bool embeddedCommandConflict)
    {
        if (managedMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(managedMessageCount));
        }

        if (captureOverflowed || embeddedCommandConflict)
        {
            return AdvanceWindowAdmission.Reject;
        }

        return closeKind switch
        {
            AdvanceWindowCloseKind.Completed => AdvanceWindowAdmission.Standard,
            AdvanceWindowCloseKind.SupersededByOverlappingPrefix when managedMessageCount > 0 =>
                AdvanceWindowAdmission.SupersededLeaseOnly,
            _ => AdvanceWindowAdmission.Reject
        };
    }

    /// <summary>
    /// One cascade must dispatch at most once. A superseded window shares its
    /// drain pass with the sibling window carrying the same identity, so a
    /// second authorization of the SAME generation inside the SAME managed
    /// update is suppressed. Green replays of the same generation arrive in a
    /// later update with a new window sequence and stay authorized.
    /// </summary>
    public static bool IsSameDrainDuplicate(
        long lastAuthorizedUpdate,
        long lastAuthorizedGeneration,
        long update,
        long generation) =>
        lastAuthorizedGeneration == generation && lastAuthorizedUpdate == update;
}
