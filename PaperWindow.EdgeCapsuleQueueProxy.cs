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

        // Pointer morphs begin before the target provider is asked for its final card size. Reserve
        // the documented preview ceiling here, not the previous/default request, so the same queue
        // HWND can accept a wider or taller A-to-B successor without moving its visible root.
        var workAreaDip = layout.Monitor.LocalWorkAreaDip;
        var maximumPreview = new EdgeCapsulePreviewSize(
            EdgeCapsulePreviewSize.MaximumWidthDip,
            EdgeCapsulePreviewSize.MaximumHeightDip).Normalize(
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumWidthDip,
                    workAreaDip.Width - 16),
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumHeightDip,
                    workAreaDip.Height - 16));
        var previewBodyWidth = Math.Max(
            1,
            maximumPreview.WidthDip -
            layout.MaximumCloseWidthDip);
        var preview = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                layout.NormalTopDip,
                previewBodyWidth,
                layout.MaximumCloseWidthDip,
                maximumPreview.HeightDip));
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

    internal bool TryGetEdgeCapsuleQueueProxySnapshotSource(
        out EdgeCapsulePresentationFrame source)
    {
        source = EdgeCapsulePresentationFrame.Hidden;
        return _windowLifecycle == PaperWindowLifecycleState.Alive &&
            !IsClosed &&
            _edgeCapsuleHost?.TryGetAppliedPresentation(out source) == true &&
            source.Visible &&
            source.IsUsable;
    }

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

    internal bool PrepareEdgeCapsuleQueueProxyEndpointLayoutForHandoff() =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        !IsClosed &&
        (_edgeCapsuleHost?
            .PrepareCompositionSourceLayoutForBatchHandoff() ?? false);

    internal bool TryApplyLatestEdgeCapsuleQueueProxyEndpoint(
        out EdgeCapsulePresentationFrame endpoint)
    {
        endpoint = EdgeCapsulePresentationFrame.Hidden;
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return _edgeCapsuleHost?.Handle is not { } handle ||
                !WindowNative.IsWindowHandleAlive(handle);
        }

        endpoint = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        return ApplyEdgeCapsuleQueueProxyEndpoint(endpoint);
    }

    internal bool VerifyEdgeCapsuleQueueProxyEndpoint(
        EdgeCapsulePresentationFrame endpoint)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return _edgeCapsuleHost?.Handle is not { } handle ||
                !WindowNative.IsWindowHandleAlive(handle);
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
