namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal EdgeCapsuleVisualAuthority CurrentEdgeCapsuleVisualAuthority
    {
        get
        {
            var floatingCoverActive =
                _deepCapsuleFloatingDragHost is { IsVisible: true };
            return EdgeCapsuleQueueProxyPolicy.ResolveVisualAuthority(
                _edgeCapsule.State.Gesture,
                floatingCoverActive,
                _controller.IsEdgeCapsuleQueueProxyRetainingSource(
                    this));
        }
    }

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
        var sourceReady = retainedByCurrentProxy
            ? host.MatchesQueueTranslationSurface(source)
            : host.MatchesPresentation(source);
        return new EdgeCapsuleQueueProxyCandidate(
            _paper.Id,
            queueKey,
            start,
            source,
            target,
            motion,
            host.Handle != IntPtr.Zero &&
                sourceReady &&
                !IsExperimentalPassive &&
                !_advancedInteractionLocked &&
                !_controller.State.ExperimentalDockedCapsulesNonTopmost &&
                _controller.FullscreenAvoidanceWindowForQueue(
                    _paper.CapsuleMonitorDeviceName) == IntPtr.Zero,
            host.IsTopmost,
            retainedByCurrentProxy,
            CurrentEdgeCapsuleVisualAuthority);
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

        // This is output-HWND capacity, not a WPF backing surface.
        // Reserve the legal queue envelope so different plugin card sizes
        // can replace one another without moving an active DComp target.
        var workAreaDip = layout.Monitor.LocalWorkAreaDip;
        var maximumPreview = new EdgeCapsulePreviewSize(
            EdgeCapsulePreviewSize.MaximumWidthDip,
            EdgeCapsulePreviewSize.MaximumHeightDip)
            .Normalize(
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

    internal bool
        PrepareEdgeCapsuleQueueProxyEndpointLayoutForHandoff() =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        !IsClosed &&
        (_edgeCapsuleHost?
            .PrepareCompositionSourceLayoutForBatchHandoff() ??
         false);

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

        // Settle Measure/Pointer/Presentation while the queue cover is still authoritative. The
        // controller calls this method before real HWNDs are revealed, so any final text-width or
        // device-pixel correction remains hidden instead of becoming a one-pixel post-handoff nudge.
        FlushEdgeCapsuleQueueProxyEndpoint();

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

        var latestEndpoint = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        var presenterSettled =
            !_edgeCapsule.HasActiveTransition &&
            _edgeCapsule.AppliedPresentation == endpoint &&
            latestEndpoint == endpoint;
#if DEBUG
        if (!presenterSettled)
        {
            var applied = _edgeCapsule.AppliedPresentation;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=presenter-mismatch " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"transition={_edgeCapsule.HasActiveTransition} " +
                $"applied={applied.Surface}:{applied.Bounds.Width}x{applied.Bounds.Height} " +
                $"endpoint={endpoint.Surface}:{endpoint.Bounds.Width}x{endpoint.Bounds.Height} " +
                $"latest={latestEndpoint.Surface}:{latestEndpoint.Bounds.Width}x{latestEndpoint.Bounds.Height}");
        }
#endif
        if (!presenterSettled)
        {
            return false;
        }

        return endpoint.Visible
            ? _edgeCapsuleHost?.MatchesPresentation(endpoint) == true
            : _edgeCapsuleHost == null ||
              _edgeCapsuleHost.MatchesPresentation(endpoint);
    }

    internal void InvalidateEdgeCapsuleQueueProxyPointer()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        var pointer = CaptureEdgeCapsulePointerPosition();
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

        var latestEndpoint = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        var hostAlreadyMatches = latestEndpoint.Visible
            ? _edgeCapsuleHost?.MatchesPresentation(latestEndpoint) == true
            : _edgeCapsuleHost == null ||
              _edgeCapsuleHost.MatchesPresentation(latestEndpoint);
        if (!_edgeCapsule.HasActiveTransition &&
            _edgeCapsule.AppliedPresentation == latestEndpoint &&
            hostAlreadyMatches)
        {
            // The successful handoff path calls this once more after releasing the DComp cover.
            // A verified settled endpoint must be a no-op there; recomputing layout after reveal is
            // exactly the visible final-pixel correction this barrier is intended to eliminate.
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
