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
        return _controller.TryStageEdgeCapsuleVisualTransaction(
            this,
            motion,
            refreshLayout);
    }

    internal void CommitEdgeCapsuleVisualTransaction(
        EdgeCapsuleMotion motion,
        bool refreshLayout,
        long transactionTimestamp)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement))
        {
            return;
        }

        _edgeCapsule.RequestPresentation(motion);
        var dirty = EdgeCapsuleDirty.Presentation;
        if (refreshLayout)
        {
            dirty |= EdgeCapsuleDirty.Measure;
        }

        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Flush(
            dirty,
            dispatcher,
            ReconcileEdgeCapsule,
            transactionTimestamp);
    }

    internal void RetryEdgeCapsuleVisualTransaction()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return;
        }

        _edgeCapsule.ForceApplyCurrentPresentation();
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
    }
}
