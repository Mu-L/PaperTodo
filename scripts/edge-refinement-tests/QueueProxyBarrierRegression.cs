using System.IO;
using System.Runtime.CompilerServices;

namespace PaperTodo;

/// <summary>
/// Small source-level guards for native synchronization rules that policy-only tests cannot
/// observe. Keep these checks on ownership ordering, not on incidental method structure.
/// </summary>
internal static class QueueProxyBarrierRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var root = RepositoryRoot();
        var startup = Read(root, "EdgeCapsuleQueueCompositionProxy.Startup.cs");
        var visuals = Read(root, "EdgeCapsuleQueueCompositionProxy.Visuals.cs");
        var handoff = Between(
            Read(root, "EdgeCapsuleQueueCompositionProxy.Handoff.cs"),
            "public bool TryReleaseForHandoff()",
            "public bool ReleaseAfterCoverLoss()");
        var nativeBatch = Between(
            Read(root, "WindowNative.cs"),
            "internal static bool TrySetWindowCloakedBatch(",
            "private static bool TrySetWindowCloakAttribute(");
        var closeForDrag = Between(
            Read(root, "AppController.EdgeCapsulePreview.cs"),
            "internal bool CloseEdgeCapsulePreviewForDrag(",
            "internal void CloseEdgeCapsulePreviewForClose(");
        var runtimeFailure = Between(
            Read(root, "EdgeCapsuleQueueCompositionProxy.Runtime.cs"),
            "private void InvalidateAndDrain(",
            "private void RetireInvalidHostIfIdle(");
        var sharedRuntimeLoss = Between(
            Read(root, "EdgeCapsuleQueueCompositionProxy.Routing.cs"),
            "private void HandleSharedRuntimeLost()",
            "private static DeviceScreenPoint MapPoint(");

        Assert(
            Count(startup, "TrySetWindowCloakedBatch") == 1 &&
            Count(startup, "WaitForCommitCompletion") == 1 &&
            !startup.Contains("TrySetWindowCloaked(", StringComparison.Ordinal),
            "startup must retain one root wait and one verified queue cloak batch");

        var endpointStage = startup[RequiredIndex(startup, "var endpointMembers =")..];
        var render = RequiredIndex(endpointStage, "Dispatcher.Invoke(");
        var endpointBarrier = RequiredIndex(
            endpointStage,
            "FlushDesktopComposition");
        var verifyLoop = RequiredIndex(
            endpointStage,
            "foreach (var member in nativeRevealMembers)");
        Assert(
            Count(endpointStage, "FlushDesktopComposition") == 1 &&
            render < endpointBarrier && endpointBarrier < verifyLoop,
            "endpoint publish must render and flush once before per-member verification");
        Assert(
            Count(visuals, "RoundedBodyClipRadius(") == 2 &&
            Count(startup, "RoundedBodyClipForVisibleBounds(") == 6,
            "reveal/conceal clips must keep start and endpoint on one rounded silhouette");
        Assert(
            visuals.Contains(
                "SetBitmapInterpolationMode(\n                BitmapInterpolationMode.Linear)",
                StringComparison.Ordinal) &&
            visuals.Contains(
                "SetBorderMode(BorderMode.Soft)",
                StringComparison.Ordinal),
            "every proxy layer must explicitly use linear sampling and antialiased clip edges");
        Assert(
            visuals.Contains(
                "layer == EdgeCapsuleQueueProxyVisualLayer.StartSnapshot\n                        ? 0",
                StringComparison.Ordinal),
            "the old snapshot outline must disappear atomically when the endpoint layer starts");

        var previewModel = Read(root, "PaperWindow.EdgeCapsulePreview.cs");
        var previewOpen = Between(
            previewModel,
            "internal bool SetEdgeCapsulePreviewOpen(",
            "internal void SetEdgeCapsulePreviewClosed(");
        var previewClose = Between(
            previewModel,
            "internal void SetEdgeCapsulePreviewClosed(",
            "internal void ClearEdgeCapsulePreviewContent(");
        Assert(
            !previewOpen.Contains(
                "CompleteEdgeCapsuleQueueCompositionProxyFor",
                StringComparison.Ordinal) &&
            !previewClose.Contains(
                "CompleteEdgeCapsuleQueueCompositionProxyFor",
                StringComparison.Ordinal),
            "preview model changes must leave the active hover cover available to a successor");

        var topmostRefresh = Between(
            Read(root, "PaperWindow.cs"),
            "internal void RefreshDeepCapsuleSlotTopmost()",
            "private void RefreshPaperIconButton");
        Assert(
            RequiredIndex(topmostRefresh, "WouldChangeZOrder(") <
                RequiredIndex(
                    topmostRefresh,
                    "CompleteEdgeCapsuleQueueCompositionProxyFor") &&
            Count(
                topmostRefresh,
                "CompleteEdgeCapsuleQueueCompositionProxyFor") == 1,
            "ordinary placement refresh must not retire a proxy unless z-order really changes");

        var snapshotHost = Read(root, "EdgeCapsuleProxySnapshotHost.cs");
        Assert(
            snapshotHost.Contains(
                "private const int MaximumPoolSize = 2;",
                StringComparison.Ordinal),
            "A-to-B browsing needs exactly two bounded snapshot leases");

        var reveal = RequiredIndex(handoff, "TrySetWindowCloakedBatch");
        var detach = RequiredIndex(handoff, "_target.SetRoot(null!)");
        var detachCommit = RequiredIndex(handoff, "_device.Commit().CheckError()");
        var leaseRelease = RequiredIndex(handoff, "_host.Detach(this)");
        Assert(
            reveal < detach &&
            detach < detachCommit &&
            detachCommit < leaseRelease &&
            handoff.Contains("Cloaked: false", StringComparison.Ordinal) &&
            !handoff.Contains("WaitForCommitCompletion", StringComparison.Ordinal) &&
            !handoff.Contains("TrySetWindowCloaked(", StringComparison.Ordinal),
            "handoff must reveal, detach and release the queue lease before deferred cleanup");
        var cleanupFailure = handoff[RequiredIndex(handoff, "catch (Exception ex)")..];
        Assert(
            !cleanupFailure.Contains("Cloaked: true", StringComparison.Ordinal),
            "cleanup failure after verified reveal must not re-cloak real HWNDs");

        var successfulBatch = nativeBatch[..RequiredIndex(nativeBatch, "if (!verified)")];
        var set = RequiredIndex(successfulBatch, "TrySetWindowCloakAttribute");
        var flush = RequiredIndex(successfulBatch, "DwmFlush()");
        var verify = RequiredIndex(successfulBatch, "TryGetWindowAppCloaked");
        Assert(
            Count(successfulBatch, "DwmFlush()") == 1 &&
            set < flush && flush < verify,
            "a successful cloak batch must write all states, flush once, then verify");

        Assert(
            closeForDrag.Contains(
                "CloseEdgeCapsulePreview(animate: true, arrange: true)",
                StringComparison.Ordinal),
            "drag threshold must stage the preview close animation");
        foreach (var forbidden in new[]
                 {
                     "FlushEdgeCapsulePreviewCompactPresentation",
                     "FlushEdgeCapsulePresentation",
                     "FlushDesktopComposition",
                     "DwmFlush(",
                     "WaitForCommitCompletion",
                     "Dispatcher.Invoke(",
                     ".Wait("
                 })
        {
            Assert(
                !closeForDrag.Contains(forbidden, StringComparison.Ordinal),
                $"preview close for drag must not synchronously wait: {forbidden}");
        }

        Assert(
            runtimeFailure.Contains("HandleSharedRuntimeLost", StringComparison.Ordinal) &&
            runtimeFailure.Contains("RetireInvalidHostIfIdle", StringComparison.Ordinal) &&
            !runtimeFailure.Contains("_hosts.Clear", StringComparison.Ordinal),
            "device loss must ask active queues to reveal before runtime disposal");
        Assert(
            RequiredIndex(sharedRuntimeLoss, "_coverLost = true") <
                RequiredIndex(sharedRuntimeLoss, "BeginInvoke") &&
            sharedRuntimeLoss.Contains("CompleteNow(success: false)", StringComparison.Ordinal),
            "an affected queue must stop routing before its deferred source reveal");
    }

    private static string Read(string root, string path) =>
        File.ReadAllText(Path.Combine(root, path));

    private static string Between(string source, string start, string end)
    {
        var startIndex = RequiredIndex(source, start);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert(endIndex > startIndex, $"source marker not found: {end}");
        return source[startIndex..endIndex];
    }

    private static int RequiredIndex(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert(index >= 0, $"source marker not found: {value}");
        return index;
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0;
             (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }
        return count;
    }

    private static string RepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        foreach (var seed in new[]
                 {
                     Path.GetDirectoryName(sourcePath),
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = string.IsNullOrWhiteSpace(seed)
                     ? null
                     : new DirectoryInfo(seed);
                 directory != null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "EdgeCapsuleQueueCompositionProxy.Startup.cs")))
                {
                    return directory.FullName;
                }
            }
        }
        throw new InvalidOperationException("cannot locate the PaperTodo source root");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
