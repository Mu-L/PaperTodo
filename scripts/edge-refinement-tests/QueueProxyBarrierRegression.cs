using System.IO;

internal static class QueueProxyBarrierRegression
{
    public static void Run()
    {
        var startup = Source(
            "EdgeCapsuleQueueCompositionProxy.Startup.cs");
        var handoff = Source(
            "EdgeCapsuleQueueCompositionProxy.Handoff.cs");
        var native = Source("WindowNative.cs");
        var controller = Source(
            "AppController.EdgeCapsuleVisualTransaction.cs");

        Require(
            startup.Contains(
                "TrySetWindowCloakedBatchDetailed") &&
            startup.Contains("PublishBeforeFlush") &&
            startup.Contains("_endpointCommitRequested"),
            "startup root, cloak and endpoint must share one boundary");
        Require(
            handoff.Contains(
                "WindowCloakBatchResult.RollbackFailed") &&
            handoff.Contains("ReleaseAfterCoverLoss"),
            "rollback loss must reveal real authority immediately");
        Require(
            native.Contains("RollbackFailed") &&
            native.Contains("RolledBack"),
            "cloak transaction must distinguish rollback outcomes");

        var endpointApply = controller.IndexOf(
            "TryApplyLatestEdgeCapsuleQueueProxyEndpoint",
            StringComparison.Ordinal);
        var batchBegin = endpointApply < 0
            ? -1
            : controller.LastIndexOf(
                "BeginWindowDeviceBoundsBatch",
                endpointApply,
                StringComparison.Ordinal);
        var handoffRelease = endpointApply < 0
            ? -1
            : controller.IndexOf(
                "TryReleaseForHandoff",
                endpointApply,
                StringComparison.Ordinal);
        Require(
            batchBegin >= 0 &&
            batchBegin < endpointApply &&
            handoffRelease > endpointApply,
            "handoff endpoints must be submitted as one native batch before cover release");
    }

    private static string Source(string name)
    {
        var directory =
            new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(
                   directory.FullName,
                   "PaperTodo.csproj")))
        {
            directory = directory.Parent;
        }
        if (directory == null)
        {
            throw new InvalidOperationException(
                "repository root not found");
        }
        return File.ReadAllText(
            Path.Combine(directory.FullName, name));
    }

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
