using System.Windows.Media.Imaging;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private int? _edgeCapsuleProxyPreviewContentReleaseGeneration;

    internal EdgeCapsuleQueueProxyCandidate? CaptureEdgeCapsuleQueueProxyCandidate(
        string queueKey,
        EdgeCapsuleMotion motion)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            _edgeCapsuleHost is not { IsVisible: true } host)
        {
            return null;
        }

        var start = _edgeCapsule.AppliedPresentation;
        var target = _edgeCapsule
            .PlanTargetPresentation(CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        if (EdgeCapsuleQueueProxyPolicy.IsEnabled && !start.Bounds.IsEmpty)
        {
            // Consume the dispatcher-prewarmed output and bind it to this queue before snapshot
            // capture. Subsequent edge animations reuse the same HWND and DComp target.
            EdgeCapsuleQueueCompositionProxy.PrewarmQueue(
                host.Dispatcher,
                queueKey,
                host.IsTopmost,
                EdgeCapsuleQueueProxyGeometry.OutputBounds(start.Bounds));
        }

        return new EdgeCapsuleQueueProxyCandidate(
            _paper.Id,
            queueKey,
            start,
            target,
            motion,
            host.Handle != IntPtr.Zero &&
                host.MatchesPresentation(start) &&
                !IsExperimentalPassive &&
                !_advancedInteractionLocked &&
                !_controller.State.ExperimentalDockedCapsulesNonTopmost &&
                _controller.FullscreenAvoidanceWindowForQueue(
                    _paper.CapsuleMonitorDeviceName) == IntPtr.Zero,
            host.IsTopmost);
    }

    internal IntPtr EdgeCapsuleQueueProxySourceHandle =>
        _edgeCapsuleHost?.Handle ?? IntPtr.Zero;
    internal bool CanRouteEdgeCapsuleQueueProxyInput => CanEnterEdgeCapsulePreview;

    internal BitmapSource? CaptureEdgeCapsuleQueueProxySnapshot(
        EdgeCapsulePresentationFrame source) =>
        _edgeCapsuleHost?.CaptureProxySnapshot(source);

    internal bool ApplyEdgeCapsuleQueueProxyEndpoint(
        EdgeCapsulePresentationFrame endpoint)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return false;
        }
        if (!endpoint.Visible)
        {
            return _edgeCapsuleHost?.Apply(endpoint) ?? true;
        }
        return EnsureDeepCapsuleSlotHost().Apply(endpoint);
    }

    internal bool PrepareEdgeCapsuleQueueProxyEndpointForHandoff() =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        !IsClosed &&
        (_edgeCapsuleHost?.PrepareCompositionSourceForHandoff() ?? false);

    internal bool TryApplyLatestEdgeCapsuleQueueProxyEndpoint()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return _edgeCapsuleHost?.Handle is not { } handle ||
                !WindowNative.IsWindowHandleAlive(handle);
        }
        var endpoint = _edgeCapsule
            .PlanTargetPresentation(CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        if (!ApplyEdgeCapsuleQueueProxyEndpoint(endpoint))
        {
            return false;
        }
        if (endpoint.Visible && !PrepareEdgeCapsuleQueueProxyEndpointForHandoff())
        {
            return false;
        }
        return endpoint.Visible
            ? _edgeCapsuleHost?.MatchesPresentation(endpoint) == true
            : _edgeCapsuleHost == null || _edgeCapsuleHost.MatchesPresentation(endpoint);
    }

    internal void ReleaseDeferredEdgeCapsuleQueueProxyPreviewContent()
    {
        if (_edgeCapsuleProxyPreviewContentReleaseGeneration is not { } generation)
        {
            return;
        }
        _edgeCapsuleProxyPreviewContentReleaseGeneration = null;
        if (generation != _edgeCapsulePreviewContentGeneration ||
            _edgeCapsulePreviewRequest != null ||
            IsEdgeCapsulePreviewOpen)
        {
            return;
        }
        _edgeCapsuleHost?.ClearPreviewContent();
    }

    internal void MarkEdgeCapsuleQueueProxyPreviewContentReleasePending() =>
        _edgeCapsuleProxyPreviewContentReleaseGeneration =
            _edgeCapsulePreviewContentGeneration;

    internal void InvalidateEdgeCapsuleQueueProxyPointer()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return;
        }

        var pointer = CaptureEdgeCapsulePointerPosition();
        // While real source HWNDs are cloaked, this timer is the physical-pointer wake-up path.
        // Route it through the same priming seam as native host input so a reverse hover resize can
        // install its next compositor owner instead of falling back to visible HWND frames.
        _controller.NotifyEdgeCapsulePreviewPhysicalPointer(this, pointer);
        InvalidateEdgeCapsulePointer();
    }

    internal void FlushEdgeCapsuleQueueProxyEndpoint()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return;
        }
        _edgeCapsule.CancelTransition();
        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure |
            EdgeCapsuleDirty.Pointer);
    }
}