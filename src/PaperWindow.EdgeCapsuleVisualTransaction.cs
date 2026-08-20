namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool TryStageEdgeCapsuleVisualTransaction(
        bool animate,
        EdgeCapsuleTransitionReason reason,
        int durationMilliseconds = EdgeCapsuleLayout.SlotMoveMilliseconds,
        bool refreshLayout = false)
    {
        animate = animate && _controller.State.EnableAnimations;
        var motion = animate
            ? EdgeCapsuleMotion.Animate(reason, durationMilliseconds)
            : EdgeCapsuleMotion.Snap(reason);
        var staged = _controller.TryStageEdgeCapsuleVisualTransaction(
            this,
            motion,
            refreshLayout);
#if DEBUG
        if (EdgeCapsuleRetractionDiagnostics.IsActive &&
            (reason == EdgeCapsuleTransitionReason.Retraction ||
             IsDeepCapsuleRetractedIntoMaster ||
             IsDeepCapsuleSlotRetracting))
        {
            EdgeCapsuleRetractionDiagnostics.Trace(
                "visual-stage",
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"staged={staged} motion={motion.Kind}/{motion.Reason}/{motion.DurationMilliseconds}ms " +
                $"refreshLayout={refreshLayout} slot={EdgeCapsuleSlot} " +
                $"applied={_edgeCapsule.AppliedPresentation.Surface}:" +
                $"{_edgeCapsule.AppliedPresentation.Bounds.Top}/" +
                $"{_edgeCapsule.AppliedPresentation.Opacity:F4}");
        }
#endif
        return staged;
    }

    internal void JoinEdgeCapsuleNativeTransactionGroup(long groupId) =>
        _edgeCapsule.JoinNativeBatchTransactionGroup(groupId);

    internal EdgeCapsuleNativeBatchApplyStatus CommitEdgeCapsuleVisualTransaction(
        EdgeCapsuleMotion motion,
        bool refreshLayout,
        long transactionTimestamp,
        bool rebaseActiveTransition)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement))
        {
            return EdgeCapsuleNativeBatchApplyStatus.Ready;
        }

#if DEBUG
        var traceRetraction =
            EdgeCapsuleRetractionDiagnostics.IsActive &&
            (motion.Reason == EdgeCapsuleTransitionReason.Retraction ||
             IsDeepCapsuleRetractedIntoMaster ||
             IsDeepCapsuleSlotRetracting);
        if (traceRetraction)
        {
            var before = _edgeCapsule.AppliedPresentation;
            EdgeCapsuleRetractionDiagnostics.Trace(
                "visual-commit-begin",
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"motion={motion.Kind}/{motion.Reason}/{motion.DurationMilliseconds}ms " +
                $"refreshLayout={refreshLayout} rebase={rebaseActiveTransition} " +
                $"slot={EdgeCapsuleSlot} activeTransition={_edgeCapsule.HasActiveTransition} " +
                $"applied={before.Surface}:{before.Bounds.Top}/{before.Opacity:F4} " +
                $"hostTop={before.HostBounds.Top}");
        }
#endif

        _edgeCapsule.BeginNativeBatchApply();
        _edgeCapsule.RequestPresentation(
            motion,
            rebaseActiveTransition);
        var dirty = EdgeCapsuleDirty.Presentation;
        if (refreshLayout)
        {
            dirty |= EdgeCapsuleDirty.Measure;
        }

        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsuleVisualTransactionNotificationDeferred = true;
        try
        {
            _edgeCapsule.Flush(
                dirty,
                dispatcher,
                ReconcileEdgeCapsule,
                transactionTimestamp);
        }
        finally
        {
            _edgeCapsuleVisualTransactionNotificationDeferred = false;
        }
#if DEBUG
        if (traceRetraction)
        {
            var after = _edgeCapsule.AppliedPresentation;
            EdgeCapsuleRetractionDiagnostics.Trace(
                "visual-commit-end",
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"status={_edgeCapsule.NativeBatchApplyStatus} " +
                $"activeTransition={_edgeCapsule.HasActiveTransition} " +
                $"applied={after.Surface}:{after.Bounds.Top}/{after.Opacity:F4} " +
                $"hostTop={after.HostBounds.Top} version={_edgeCapsule.AppliedPresentationVersion}");
        }
#endif
        return _edgeCapsule.NativeBatchApplyStatus;
    }

    internal void RebaseEdgeCapsuleQueueProxyAnimationClock(
        long transactionTimestamp)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        _edgeCapsule.RebaseActiveTransitionStart(
            transactionTimestamp);
    }

    internal void CompleteEdgeCapsuleVisualTransactionApply(
        bool success,
        bool deferred,
        long transactionTimestamp)
    {
#if DEBUG
        if (EdgeCapsuleRetractionDiagnostics.IsActive &&
            (IsDeepCapsuleRetractedIntoMaster ||
             IsDeepCapsuleSlotRetracting ||
             _edgeCapsule.AppliedPresentation.Surface is
                 EdgeCapsuleSurfaceKind.DockedRetracted or
                 EdgeCapsuleSurfaceKind.DockedRetracting))
        {
            EdgeCapsuleRetractionDiagnostics.Trace(
                "visual-apply-complete",
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"success={success} deferred={deferred} " +
                $"activeTransition={_edgeCapsule.HasActiveTransition} " +
                $"retryPending={_edgeCapsule.NativeBatchRetryPending}");
        }
#endif
        if (success)
        {
            _edgeCapsule.CompleteNativeBatchApplySuccess();
            return;
        }
        if (deferred)
        {
            _edgeCapsule.CompleteNativeBatchApplyDeferred();
            return;
        }

        // Re-enter through the shared frame scheduler. A temporary cross-queue transaction group
        // keeps its related queues in one native batch; ordinary queues still retry independently.
        _edgeCapsule.CompleteNativeBatchApplyFailure(transactionTimestamp);
    }
}
