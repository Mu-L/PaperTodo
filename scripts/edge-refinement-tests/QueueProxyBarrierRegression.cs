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
        var presenter = Source("EdgeCapsulePresenter.cs");
        var hostPolicy = Source("PaperWindow.EdgeCapsule.cs");
        var preview = Source("PaperWindow.EdgeCapsulePreview.cs");
        var placement = Source("PaperWindow.EdgeCapsulePlacement.cs");
        var interaction = Source("PaperWindow.EdgeCapsuleInteraction.cs");
        var dragWindow = Source("EdgeCapsuleDragWindow.cs");
        var frameScheduler = Source("EdgeCapsuleFrameScheduler.cs");
        var visuals = Source(
            "EdgeCapsuleQueueCompositionProxy.Visuals.cs");

        var coldGuard = startup.IndexOf(
            "if (_predecessor == null)",
            StringComparison.Ordinal);
        var staticRoot = coldGuard < 0
            ? -1
            : startup.IndexOf(
                "_target.SetRoot(_root)",
                coldGuard,
                StringComparison.Ordinal);
        var outputShow = startup.IndexOf(
            "_window.Show(_outputBounds, _plan.Topmost)",
            StringComparison.Ordinal);
        var coverFlush = startup.IndexOf(
            "WindowNative.TryFlushDesktopComposition()",
            StringComparison.Ordinal);
        var publishCallback = startup.IndexOf(
            "bool PublishBeforeFlush()",
            StringComparison.Ordinal);
        var endpointCommit = publishCallback < 0
            ? -1
            : startup.IndexOf(
                "_endpointCommitRequested(endpointTimestamp)",
                publishCallback,
                StringComparison.Ordinal);
        var animationClock = endpointCommit < 0
            ? -1
            : startup.IndexOf(
                "animationTimestamp = Stopwatch.GetTimestamp()",
                endpointCommit,
                StringComparison.Ordinal);
        var wpfClockRebase = animationClock < 0
            ? -1
            : startup.IndexOf(
                "_animationStartRequested(animationTimestamp)",
                animationClock,
                StringComparison.Ordinal);
        var animationAttach = wpfClockRebase < 0
            ? -1
            : startup.IndexOf(
                "ConfigureAnimations(animationTimestamp)",
                wpfClockRebase,
                StringComparison.Ordinal);
        var successorRoot = staticRoot < 0
            ? -1
            : startup.IndexOf(
                "_target.SetRoot(_root)",
                staticRoot + 1,
                StringComparison.Ordinal);
        var successorUnion = startup.IndexOf(
            "else if (newHandles.Length > 0)",
            StringComparison.Ordinal);
        var successorUnionRoot = successorUnion < 0
            ? -1
            : startup.IndexOf(
                "_successorAdmissionCover.Root",
                successorUnion,
                StringComparison.Ordinal);
        var successorUnionFlush = successorUnionRoot < 0
            ? -1
            : startup.IndexOf(
                "WindowNative.TryFlushDesktopComposition()",
                successorUnionRoot,
                StringComparison.Ordinal);
        var cloakBoundary = startup.IndexOf(
            "EdgeCapsuleColdStartDiagnostics.Boundary(\"before-cloak-batch\")",
            StringComparison.Ordinal);
        var cloakBatch = startup.IndexOf(
            "TrySetWindowCloakedBatchDetailed",
            StringComparison.Ordinal);

        Require(
            coldGuard >= 0 &&
            staticRoot > coldGuard &&
            outputShow > staticRoot &&
            coverFlush > outputShow &&
            cloakBoundary > coverFlush &&
            cloakBatch > cloakBoundary,
            "cold startup must publish and flush a static DComp cover before real HWND cloak");
        Require(
            startup.Contains("predecessor-cover-retained") &&
            startup.Contains("successor-union-cover-visible") &&
            startup.Contains("successor-root-staged") &&
            startup.Contains("CreateSuccessorAdmissionCover") &&
            successorUnion >= 0 &&
            successorUnionRoot > successorUnion &&
            successorUnionFlush > successorUnionRoot &&
            successorUnionFlush < cloakBoundary &&
            visuals.Contains("SnapshotStaticCoverSources") &&
            visuals.Contains("_predecessor.SnapshotStaticCoverSources(timestamp)") &&
            visuals.Contains("newHandles.Contains(member.SourceHandle)") &&
            successorRoot > animationAttach &&
            successorRoot < cloakBoundary &&
            handoff.Contains("ReleaseSuccessorAdmissionCover"),
            "successor startup must retain predecessor authority and pre-cover newly cloaked sources until the coordinated root boundary");
        Require(
            startup.Contains("outgoingHandles") &&
            startup.Contains("newHandles") &&
            endpointCommit >= 0 &&
            animationClock > endpointCommit &&
            wpfClockRebase > animationClock &&
            animationAttach > wpfClockRebase,
            "endpoint settlement must precede one shared post-endpoint WPF/DComp animation clock");
        Require(
            presenter.Contains("RebaseActiveTransitionStart") &&
            presenter.Contains("StartedAtTimestamp = startedAtTimestamp") &&
            controller.Contains("animationStartRequested: timestamp =>") &&
            controller.Contains("RebaseEdgeCapsuleQueueProxyAnimationClock"),
            "queue WPF transitions must be rebased to the compositor's post-endpoint QPC");

        var prepareCapacity = preview.IndexOf(
            "if (!PrepareEdgeCapsuleHostCapacity(size))",
            StringComparison.Ordinal);
        var contentCreate = preview.IndexOf(
            "descriptor.CreateContent(size)",
            StringComparison.Ordinal);
        Require(
            Count(
                placement,
                "ReserveEdgeCapsulePreviewCapacityBeforeFirstShow();") >= 2 &&
            preview.Contains(
                "ReserveEdgeCapsulePreviewCapacityBeforeFirstShow") &&
            preview.Contains("provider.Describe(") &&
            prepareCapacity >= 0 &&
            contentCreate > prepareCapacity &&
            preview.Contains(
                "if (!PrepareEdgeCapsuleHostCapacity(request.Size))") &&
            preview.Contains("reason=visible-host-capacity-growth") &&
            hostPolicy.Contains("CurrentEdgeCapsuleHostCapacityContains") &&
            hostPolicy.Contains("visible-growth-rejected"),
            "preview capacity must be reserved before first show and rejected before content creation if a visible generation cannot grow");

        var deferredCapacity = preview.IndexOf(
            "TryGetDeferredPluginPreviewCapacity",
            StringComparison.Ordinal);
        var providerResolve = deferredCapacity < 0
            ? -1
            : preview.IndexOf(
                "provider = ResolveEdgeCapsulePreviewProvider()",
                deferredCapacity,
                StringComparison.Ordinal);
        Require(
            deferredCapacity >= 0 &&
            providerResolve > deferredCapacity &&
            preview.Contains("DeferredNativePluginEnvelope") &&
            preview.Contains("DeferredWebPluginManifest") &&
            preview.Contains("PaperBodyPlugins.TryGet") &&
            preview.Contains("EdgeCapsulePreviewSize.MaximumWidthDip"),
            "deferred plugin papers must reserve bounded plugin capacity before the temporary Default provider can publish the first Host");

        var configureStart = dragWindow.IndexOf(
            "private bool ConfigureForReuse",
            StringComparison.Ordinal);
        var resetStart = configureStart < 0
            ? -1
            : dragWindow.IndexOf(
                "private void ResetTransientPresentation",
                configureStart,
                StringComparison.Ordinal);
        var configureBody = configureStart >= 0 && resetStart > configureStart
            ? dragWindow[configureStart..resetStart]
            : "";
        Require(
            Count(
                placement,
                "QueueDeepCapsuleFloatingDragInfrastructurePrewarm(") >= 2 &&
            Count(
                placement,
                "DispatcherPriority.ApplicationIdle") >= 2 &&
            !hostPolicy.Contains(
                "QueueDeepCapsuleFloatingDragInfrastructurePrewarm") &&
            interaction.Contains("NeedsInfrastructurePrewarm") &&
            interaction.Contains("TryPrewarmInfrastructure") &&
            dragWindow.Contains("private void BuildContent()") &&
            Count(dragWindow, "BuildContent();") == 1 &&
            dragWindow.Contains("private void BindOptions(") &&
            dragWindow.Contains("treeRebuilt=false") &&
            !configureBody.Contains("Content = null") &&
            !configureBody.Contains("BuildContent("),
            "floating drag must prewarm one process infrastructure host at idle and bind papers onto one persistent visual tree without hover prediction or Content replacement");

        var queueReconcileStart = presenter.IndexOf(
            "private void QueueReconcile",
            StringComparison.Ordinal);
        var runReconcileStart = queueReconcileStart < 0
            ? -1
            : presenter.IndexOf(
                "private void RunReconcile",
                queueReconcileStart,
                StringComparison.Ordinal);
        var queueReconcileBody =
            queueReconcileStart >= 0 && runReconcileStart > queueReconcileStart
                ? presenter[queueReconcileStart..runReconcileStart]
                : "";
        var buildGroupsStart = frameScheduler.IndexOf(
            "private int BuildFrameGroups",
            StringComparison.Ordinal);
        var advanceGroupStart = buildGroupsStart < 0
            ? -1
            : frameScheduler.IndexOf(
                "private bool AdvanceNativeBatchGroup",
                buildGroupsStart,
                StringComparison.Ordinal);
        var buildGroupsBody =
            buildGroupsStart >= 0 && advanceGroupStart > buildGroupsStart
                ? frameScheduler[buildGroupsStart..advanceGroupStart]
                : "";
        Require(
            queueReconcileBody.Contains("DispatcherPriority.Send") &&
            queueReconcileBody.Contains("DispatcherPriority.Render") &&
            !queueReconcileBody.Contains("DispatcherPriority.Loaded") &&
            queueReconcileBody.Contains("RegisterRenderReconcile") &&
            queueReconcileBody.Contains("CompleteRenderReconcile") &&
            frameScheduler.Contains("_pendingRenderReconciles") &&
            frameScheduler.Contains("skippedPending=") &&
            frameScheduler.Contains("skippedExternal=") &&
            frameScheduler.Contains("skippedReentrant=") &&
            frameScheduler.Contains("skipSpanMs=") &&
            frameScheduler.Contains(
                "private readonly List<List<EdgeCapsulePresenter>> _frameGroups") &&
            frameScheduler.Contains(
                "private readonly Dictionary<EdgeCapsuleNativeBatchGroup, int> _frameGroupIndices") &&
            buildGroupsBody.Contains("_frameGroupIndices.Clear()") &&
            !buildGroupsBody.Contains(
                "new Dictionary<EdgeCapsuleNativeBatchGroup, int>()") &&
            !buildGroupsBody.Contains(
                "new List<List<EdgeCapsulePresenter>>()"),
            "passive reconcile must run at Render priority without self-starving the shared frame, while frame grouping reuses scratch collections");

        Require(
            frameScheduler.Contains("TransitionLivenessWatchdogMilliseconds = 12") &&
            frameScheduler.Contains("private readonly DispatcherTimer _transitionLivenessWatchdog") &&
            frameScheduler.Contains("private DispatcherOperation? _transitionLivenessRescueOperation") &&
            frameScheduler.Contains("_transitionLivenessWatchdogGeneration") &&
            frameScheduler.Contains("_transitionLivenessWatchdogDeadlineTimestamp") &&
            frameScheduler.Contains("_transitionLivenessWatchdog.Tick += OnTransitionLivenessWatchdog") &&
            frameScheduler.Contains("nowTimestamp < _transitionLivenessWatchdogDeadlineTimestamp") &&
            frameScheduler.Contains("ScheduleTransitionLivenessWatchdog") &&
            frameScheduler.Contains("QueueExpiredTransitionLivenessRescue(\"pending-release\")") &&
            frameScheduler.Contains("DispatcherPriority.Render") &&
            frameScheduler.Contains("scheduler.pending phase=drained") &&
            frameScheduler.Contains("scheduler.watchdog phase=early") &&
            frameScheduler.Contains("scheduler.watchdog phase=queued") &&
            frameScheduler.Contains("scheduler.watchdog phase=run") &&
            frameScheduler.Contains(
                "AdvanceSharedFrame(renderingTime: null, source: \"watchdog\")") &&
            frameScheduler.Contains("committedApply=") &&
            frameScheduler.Contains("activeTransition=") &&
            !frameScheduler.Contains("!anyCommittedApply &&") &&
            Count(frameScheduler, "ArmTransitionLivenessWatchdog();") == 1 &&
            Count(frameScheduler, "_transitionLivenessWatchdog.Start();") == 1 &&
            frameScheduler.Contains("HasActiveTransitionPresenter()") &&
            frameScheduler.Contains("NativeBatchCommitVersion"),
            "transition liveness must use a Stopwatch-backed deadline, reject early timer ticks, and rescue immediately after an expired pending-reconcile barrier drains without creating a second frame clock");

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
