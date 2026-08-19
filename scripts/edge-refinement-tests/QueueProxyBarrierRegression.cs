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
        var host = Source("EdgeCapsuleHost.cs");
        var preview = Source("PaperWindow.EdgeCapsulePreview.cs");
        var placement = Source("PaperWindow.EdgeCapsulePlacement.cs");

        var staticRoot = startup.IndexOf(
            "_target.SetRoot(_root)",
            StringComparison.Ordinal);
        var outputShow = startup.IndexOf(
            "_window.Show(_outputBounds, _plan.Topmost)",
            StringComparison.Ordinal);
        var coverFlush = startup.IndexOf(
            "WindowNative.TryFlushDesktopComposition()",
            StringComparison.Ordinal);
        var cloakBoundary = startup.IndexOf(
            "EdgeCapsuleColdStartDiagnostics.Boundary(\"before-cloak-batch\")",
            StringComparison.Ordinal);
        var cloakBatch = startup.IndexOf(
            "TrySetWindowCloakedBatchDetailed",
            StringComparison.Ordinal);
        Require(
            staticRoot >= 0 &&
            outputShow > staticRoot &&
            coverFlush > outputShow &&
            cloakBoundary > coverFlush &&
            cloakBatch > cloakBoundary,
            "a static DComp root must be committed, shown and flushed before any real HWND cloak boundary");
        Require(
            startup.Contains("cover-static-visible") &&
            startup.Contains("_endpointCommitRequested") &&
            startup.Contains("ConfigureAnimations(animationTimestamp)"),
            "startup must preserve an explicit static-cover authority before endpoint-once animation publication");

        var endpointCommit = startup.IndexOf(
            "_endpointCommitRequested(animationTimestamp)",
            StringComparison.Ordinal);
        var animationAttach = startup.IndexOf(
            "ConfigureAnimations(animationTimestamp)",
            StringComparison.Ordinal);
        Require(
            endpointCommit >= 0 &&
            animationAttach > endpointCommit,
            "the fresh shared QPC must settle real endpoints before DComp animations are committed");

        Require(
            Count(
                placement,
                "ReserveEdgeCapsulePreviewCapacityBeforeFirstShow();") >= 2 &&
            preview.Contains(
                "ReserveEdgeCapsulePreviewCapacityBeforeFirstShow") &&
            preview.Contains("provider.Describe(") &&
            preview.Contains("ReserveEdgeCapsuleHostCapacity(size)") &&
            host.Contains("nativeHostSizeChanged") &&
            host.Contains("refreshNativeLayout"),
            "preview descriptor capacity must be reserved before first docked show and native-size fallback must force WPF layout");

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

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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
