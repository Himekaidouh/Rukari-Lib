namespace AzureArchive.VideoTools.Core.Commands;

public enum PreviewChainPendingAction
{
    Drop = 0,
    Retry = 1,
    Apply = 2
}

/// <summary>
/// Transient missing graph/mapping data is not successful chain coverage. A
/// pending can wait only inside its original bounded budget and current context.
/// </summary>
public static class PreviewChainRecoveryPolicy
{
    public static PreviewChainPendingAction DecidePending(
        bool contextCurrent,
        bool prerequisitesReady,
        bool retryBudgetExhausted,
        bool currentCommandsReserved = false) => !contextCurrent || currentCommandsReserved
            ? PreviewChainPendingAction.Drop
            : prerequisitesReady
                ? PreviewChainPendingAction.Apply
                : retryBudgetExhausted
                    ? PreviewChainPendingAction.Drop
                    : PreviewChainPendingAction.Retry;

    /// <summary>
    /// A successfully resolved scene with no inherited slots needs no mutation;
    /// every expected inherited slot otherwise has to finish successfully.
    /// </summary>
    public static bool HasCompleteCoverage(
        bool resolutionSucceeded,
        int expectedInheritedSlots,
        int appliedInheritedSlots,
        int failedInheritedSlots) => resolutionSucceeded
            && expectedInheritedSlots >= 0
            && appliedInheritedSlots == expectedInheritedSlots
            && failedInheritedSlots == 0;
}
