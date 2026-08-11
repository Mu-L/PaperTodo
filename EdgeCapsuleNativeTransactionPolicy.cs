namespace PaperTodo;

internal static class EdgeCapsuleNativeTransactionPolicy
{
    public static bool RequiresCrossQueueGroup(
        IEnumerable<string> queueKeys) =>
        queueKeys
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1;

    public static bool ParticipatesInBatchOutcome(
        long transactionGroupId,
        bool applyAttempted,
        bool retryWasPending,
        bool deferred) =>
        transactionGroupId > 0 ||
        applyAttempted ||
        retryWasPending ||
        deferred;

    public static bool CanRelease(
        long transactionGroupId,
        bool transitionActive,
        bool retryPending,
        bool applyActive,
        bool hasPresentationWork) =>
        transactionGroupId > 0 &&
        !transitionActive &&
        !retryPending &&
        !applyActive &&
        !hasPresentationWork;
}
