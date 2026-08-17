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
        var transaction = Between(
            Read(root, "AppController.EdgeCapsuleVisualTransaction.cs"),
            "private bool TryStartEdgeCapsuleQueueCompositionProxy(",
            "private bool PublishEdgeCapsuleQueueCompositionProxy(");
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
            Count(startup, "WaitForCommitCompletion") == 0 &&
            Count(startup, "TryFlushDesktopComposition") == 1 &&
            !startup.Contains("TrySetWindowCloaked(", StringComparison.Ordinal),
            "startup must publish the root and new source cloaks at one desktop boundary");

        var rootCommit = RequiredIndex(startup, "_target.SetRoot(_root)");
        var cloakBoundary = RequiredIndex(startup, "TrySetWindowCloakedBatch");
        var promote = RequiredIndex(startup, "_host.Promote");
        Assert(
            rootCommit < cloakBoundary && cloakBoundary < promote,
            "the predecessor must stay alive until the successor root/cloak boundary completes");

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
            Count(startup, "RoundedBodyClipForVisibleBounds(") == 8,
            "reveal/conceal clips must keep start and endpoint on one rounded silhouette");
        Assert(
            Count(startup, "PositionSurfaceForVisibleBounds(") == 5,
            "fixed native surfaces must translate with A-to-B queue reflow instead of being scaled");
        Assert(
            visuals.Contains(
                "layer == EdgeCapsuleQueueProxyVisualLayer.MovingSource",
                StringComparison.Ordinal) &&
            !visuals.Contains(
                "EdgeCapsuleQueueProxyVisualLayer.StartSnapshot)",
                StringComparison.Ordinal),
            "a full-source successor snapshot must receive the same rounded moving clip");
        Assert(
            Count(visuals, "SetBitmapInterpolationMode(") == 1 &&
            visuals.Contains(
                "BitmapInterpolationMode.Linear",
                StringComparison.Ordinal) &&
            Count(visuals, "SetBorderMode(") == 1 &&
            visuals.Contains("BorderMode.Soft", StringComparison.Ordinal),
            "every proxy layer must explicitly use linear sampling and antialiased clip edges");
        Assert(
            Count(visuals, "SetAbsoluteBeginTime(") == 1,
            "GPU animation and logical sampling must share one absolute QPC start time");
        Assert(
            visuals.Contains(
                "OpacityDurationMilliseconds =",
                StringComparison.Ordinal) &&
            visuals.Contains(
                "layer == EdgeCapsuleQueueProxyVisualLayer.StartSnapshot",
                StringComparison.Ordinal) &&
            visuals.Contains("? 0", StringComparison.Ordinal),
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
        Assert(
            !snapshotHost.Contains(
                "FlushDesktopComposition",
                StringComparison.Ordinal) &&
            !snapshotHost.Contains(
                "cloaked: false",
                StringComparison.Ordinal) &&
            snapshotHost.Contains(
                "DispatcherPriority.Render",
                StringComparison.Ordinal),
            "snapshot preparation must render while cloaked and reuse startup's desktop boundary");

        var capacity = Between(
            Read(root, "PaperWindow.EdgeCapsuleQueueProxy.cs"),
            "CaptureEdgeCapsuleQueueProxyCapacity()",
            "internal IntPtr EdgeCapsuleQueueProxySourceHandle");
        Assert(
            capacity.Contains(
                "EdgeCapsulePreviewSize.MaximumWidthDip",
                StringComparison.Ordinal) &&
            capacity.Contains(
                "EdgeCapsulePreviewSize.MaximumHeightDip",
                StringComparison.Ordinal),
            "pointer generation capacity must cover a provider size learned by its A-to-B successor");

        var snapshotCapture = RequiredIndex(
            transaction,
            "CaptureEdgeCapsuleQueueProxySnapshot(");
        var latch = RequiredIndex(
            transaction,
            "TryLatchForSuccessor");
        var latchedPlan = RequiredIndex(
            transaction,
            "var latchedPlan =");
        Assert(
            transaction[snapshotCapture..latch].Contains(
                "memberPlan.Source",
                StringComparison.Ordinal) &&
            snapshotCapture < latch && latch < latchedPlan,
            "successor snapshots must use the full source before one late queue-wide latch");

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
