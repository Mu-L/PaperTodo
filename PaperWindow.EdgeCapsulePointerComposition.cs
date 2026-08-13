namespace PaperTodo;

public sealed partial class PaperWindow
{
    /// <summary>
    /// Native/physical pointer input reaches this seam before the Presenter consumes its Pointer
    /// dirty bit. Prime only the reducer's over/out bit here so a compact shell resize can install
    /// its compositor cover before the first real HWND resize frame would otherwise be applied.
    /// The ordinary reconcile still runs immediately afterwards and remains the sole owner of the
    /// Presenter's transition/applied-frame bookkeeping.
    /// </summary>
    internal void PrimeEdgeCapsulePointerComposition(DeviceScreenPoint? pointer)
    {
        if (!EdgeCapsuleQueueProxyPolicy.IsEnabled ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            _edgeCapsuleHost is not { IsVisible: true } ||
            !HasDeepCapsuleSlotPlacement ||
            EdgeCapsuleGesture is not EdgeCapsuleGestureState.Idle)
        {
            return;
        }

        var presented = ResolveEdgeCapsulePresentedFrame(
            _edgeCapsule.AppliedPresentation);
        var pointerOver = pointer.HasValue &&
            presented.IsHitTestVisible &&
            !presented.InteractiveBounds.IsEmpty &&
            EdgeCapsuleGeometry.Contains(
                presented.InteractiveBounds,
                pointer.Value);
        if (pointerOver == _edgeCapsule.PointerOverSurface)
        {
            return;
        }

        var reduction = _edgeCapsule.Dispatch(
            EdgeCapsuleIntent.PointerSampled(pointerOver));
        if (!reduction.Changed)
        {
            return;
        }

        var target = _edgeCapsule
            .PlanTargetPresentation(CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        var start = _edgeCapsule.AppliedPresentation;
        if (!IsCompactCompositorSurface(start.Surface) ||
            !IsCompactCompositorSurface(target.Surface) ||
            start == target)
        {
            return;
        }

        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Animate(
            EdgeCapsuleTransitionReason.Pointer,
            EdgeCapsuleLayout.HorizontalResizeMilliseconds));

        // Install the DComp cover before the Send-priority pointer reconcile created by the caller.
        // If admission/start fails, the same pending Presenter motion simply follows the historical
        // fallback path and Debug diagnostics contain the exact rejection reason.
        _ = _controller.TryStartEdgeCapsulePointerCompositionProxy(this);
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
    }

    internal bool IsEdgeCapsuleQueueProxyTargetCurrent(
        EdgeCapsulePresentationFrame target)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return false;
        }
        return _edgeCapsule
            .PlanTargetPresentation(CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame() == target;
    }

    private static bool IsCompactCompositorSurface(
        EdgeCapsuleSurfaceKind surface) => surface is
        EdgeCapsuleSurfaceKind.DockedResting or
        EdgeCapsuleSurfaceKind.DockedHovered or
        EdgeCapsuleSurfaceKind.DockedActive;
}
