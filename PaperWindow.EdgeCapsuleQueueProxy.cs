using System.Windows.Media.Imaging;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private int? _edgeCapsuleProxyPreviewContentReleaseGeneration;

    internal EdgeCapsuleQueueProxyCandidate?
        CaptureEdgeCapsuleQueueProxyCandidate(
            string queueKey,
            EdgeCapsuleMotion motion,
            EdgeCapsulePresentationFrame? startOverride = null,
            EdgeCapsulePresentationFrame? sourceOverride = null,
            bool retainedByCurrentProxy = false)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            _edgeCapsuleHost is not { IsVisible: true } host)
        {
            return null;
        }

        var applied = _edgeCapsule.AppliedPresentation;
        var start = startOverride ?? applied;
        var source = sourceOverride ?? applied;
        var target = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        if (EdgeCapsuleQueueProxyPolicy.IsEnabled &&
            !source.Bounds.IsEmpty)
        {
            // Consume the dispatcher-prewarmed output and bind it to this queue before any optional
            // snapshot capture. Subsequent generations reuse the same HWND and DComp target.
            EdgeCapsuleQueueCompositionProxy.PrewarmQueue(
                host.Dispatcher,
                queueKey,
                host.IsTopmost,
                EdgeCapsuleQueueProxyGeometry.OutputBounds(
                    source.Bounds));
        }

        return new EdgeCapsuleQueueProxyCandidate(
            _paper.Id,
            queueKey,
            start,
            source,
            target,
            motion,
            host.Handle != IntPtr.Zero &&
                host.MatchesPresentation(source) &&
                !IsExperimentalPassive &&
                !_advancedInteractionLocked &&
                !_controller.State
                    .ExperimentalDockedCapsulesNonTopmost &&
                _controller.FullscreenAvoidanceWindowForQueue(
                    _paper.CapsuleMonitorDeviceName) == IntPtr.Zero,
            host.IsTopmost,
            retainedByCurrentProxy);
    }

    internal (
        DeviceScreenRect PreviewBounds,
        int MaximumDownwardShiftDevice,
        int WorkAreaBottomDevice)
        CaptureEdgeCapsuleQueueProxyCapacity()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return default;
        }

        var layout = CaptureEdgeCapsuleLayoutSnapshot();
        if (!layout.IsUsable)
        {
            return default;
        }

        var previewBodyWidth = Math.Max(
            1,
            layout.PreviewWidthDip -
            layout.MaximumCloseWidthDip);
        var preview = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                layout.NormalTopDip,
                previewBodyWidth,
                layout.MaximumCloseWidthDip,
                layout.PreviewHeightDip));
        var compact = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                layout.NormalTopDip,
                layout.RestingWidthDip,
                0,
                layout.HeightDip));
        return (
            preview.Bounds,
            Math.Max(
                0,
                preview.Bounds.Height -
                compact.Bounds.Height),
            layout.Monitor.WorkArea.Bottom);
    }

    internal IntPtr EdgeCapsuleQueueProxySourceHandle =>
        _edgeCapsuleHost?.Handle ?? IntPtr.Zero;

    internal bool CanRouteEdgeCapsuleQueueProxyInput =>
        CanEnterEdgeCapsulePreview;

    internal BitmapSource? CaptureEdgeCapsuleQueueProxySnapshot(
        EdgeCapsulePresentationFrame source) =>
        _edgeCapsuleHost?.CaptureProxySnapshot(source);

    internal bool ApplyEdgeCapsuleQueueProxyEndpoint(
        EdgeCapsulePresentationFrame endpoint)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
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
        (_edgeCapsuleHost?
            .PrepareCompositionSourceForHandoff() ?? false);

    internal bool TryApplyLatestEdgeCapsuleQueueProxyEndpoint()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return _edgeCapsuleHost?.Handle is not { } handle ||
                !WindowNative.IsWindowHandleAlive(handle);
        }

        var endpoint = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        if (!ApplyEdgeCapsuleQueueProxyEndpoint(endpoint))
        {
            return false;
        }
        if (endpoint.Visible &&
            !PrepareEdgeCapsuleQueueProxyEndpointForHandoff())
        {
            return false;
        }
        return endpoint.Visible
            ? _edgeCapsuleHost?.MatchesPresentation(endpoint) == true
            : _edgeCapsuleHost == null ||
              _edgeCapsuleHost.MatchesPresentation(endpoint);
    }

    internal void ReleaseDeferredEdgeCapsuleQueueProxyPreviewContent()
    {
        if (_edgeCapsuleProxyPreviewContentReleaseGeneration is not
            { } generation)
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
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        var pointer = CaptureEdgeCapsulePointerPosition();
        // While real source HWNDs are cloaked, this timer is the physical-pointer wake-up path.
        _controller.NotifyEdgeCapsulePreviewPhysicalPointer(
            this,
            pointer);
        InvalidateEdgeCapsulePointer();
    }

    internal void FlushEdgeCapsuleQueueProxyEndpoint()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
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
