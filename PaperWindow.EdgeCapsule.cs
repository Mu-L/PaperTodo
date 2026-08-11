using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _edgeCapsuleReconcileNotificationPending;
    private bool _edgeCapsulePresentationChangePending;
    private bool _edgeCapsulePointerOverChangePending;
    private bool _edgeCapsulePendingPointerOverBaseline;
    private bool _edgeCapsulePendingPointerOver;
    private DeviceScreenPoint? _edgeCapsulePendingPointerSample;
    private bool _edgeCapsuleVisualTransactionNotificationDeferred;

    private EdgeCapsuleHost EnsureDeepCapsuleSlotHost()
    {
        if (_edgeCapsuleHost != null)
        {
            return _edgeCapsuleHost;
        }

        _edgeCapsuleHost = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin,
            CapsuleChromeCornerRadius,
            CapsuleInnerCornerRadius,
            DeepCapsuleSlotOutlineThickness,
            DeepCapsuleSlotOutlineOverlap,
            CapsuleBodyHeight,
            CapsuleLeftPadding,
            CapsuleIconGap,
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper(),
            CapsuleLabelFontSize,
            CapsuleLabelFontWeight,
            Strings.Get("ToolTipHideThisPaper"),
            PaperBrush,
            PaperBorderBrush,
            Theme.CapsuleFocusBorderBrush,
            HoverBrush,
            BrightWeakTextBrush,
            TextBrush,
            WeakTextBrush,
            CapsuleLabelFontFamily,
            AppTypography.SymbolFontFamily,
            AppTypography.Language,
            !_controller.State.ExperimentalDockedCapsulesNonTopmost &&
            _controller.FullscreenAvoidanceWindowForQueue(
                _paper.CapsuleMonitorDeviceName) == IntPtr.Zero,
            EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)));
        var host = _edgeCapsuleHost;
        _edgeCapsule.SetNativeBatchApplyRejectedCallback(
            RejectEdgeCapsuleNativeBatchApply);
        _edgeCapsule.SetNativeBatchApplyDeferredCallback(
            RecoverDeferredEdgeCapsuleNativeBatchApply);
        host.SetExperimentalPassive(IsExperimentalPassive);
        host.SetInteractionLocked(_advancedInteractionLocked);
        AttachDeepCapsuleSlotHostInput();
        host.AttachNativeHooks(
            OnDeepCapsuleSlotHostMessage,
            CloseDeepCapsuleSlotContextMenu);
        UpdateDeepCapsuleSlotHostTheme();
        return host;
    }

    private void RejectEdgeCapsuleNativeBatchApply() =>
        _edgeCapsuleHost?.RejectNativeBatchApply();

    private void RecoverDeferredEdgeCapsuleNativeBatchApply()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return;
        }
        if (EdgeCapsuleGesture is
            EdgeCapsuleGestureState.DockedReordering or
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering)
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }

        // The gesture may have ended between the deferred frame and this Send callback. Any
        // invalidation wakes the retained shared-batch retry without consuming it standalone.
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
    }

    private IntPtr OnDeepCapsuleSlotHostMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        // WM_MOUSELEAVE / WM_NCMOUSELEAVE are the authoritative boundary signal for the real host.
        // WPF can raise MouseLeave while the physical point is still inside the applied rectangle;
        // consume the native leave as well so that a later genuine exit cannot be lost.
        if (msg is 0x02A3 or 0x02A2)
        {
            InvalidateEdgeCapsulePointerFromHostInput();
        }

        // The slot host is not a paper window. In particular it must not inherit the paper's
        // snap/maximize WM_GETMINMAXINFO handling or minimum tracking size. It only participates
        // in display-metric refresh and then lets WPF process the native message normally.
        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            if (IsDeepCapsuleDockingReveal)
            {
                // Reveal is the only atomic cross-HWND boundary. Keep the already-confirmed pair
                // unchanged for these few frames; the controller replays fresh metrics on commit.
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
                return IntPtr.Zero;
            }
            // DPI hand-off, display removal and work-area changes can all rewrite a visible HWND
            // after its logical frame was committed. Invalidate both native and measured state so
            // an unchanged target is still replayed after WPF finishes processing this message.
            InvalidateEdgeCapsuleDisplayMetrics();
            if (IsDeepCapsuleReordering)
            {
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
            }
            else
            {
                _controller.ScheduleDisplayMetricsRefresh();
            }
        }

        return IntPtr.Zero;
    }

    private void AttachDeepCapsuleSlotHostInput()
    {
        if (_edgeCapsuleHost == null)
        {
            return;
        }
        _edgeCapsuleHost.AttachInput(new EdgeCapsuleHostCallbacks(
            InvalidateEdgeCapsulePointerFromHostInput,
            OnEdgeCapsulePointerPressed,
            OnEdgeCapsulePointerMoved,
            OnEdgeCapsulePointerReleased,
            OnEdgeCapsuleCaptureLost,
            OnEdgeCapsuleCloseInvoked));
        _edgeCapsuleHost.SetContextMenu(BuildDeepCapsuleSlotContextMenu());
        RefreshDeepCapsuleSlotLabel();
    }

    private void UpdateDeepCapsuleSlotHostTheme()
    {
        _edgeCapsuleHost?.UpdateTheme(
            PaperBrush,
            PaperBorderBrush,
            Theme.CapsuleFocusBorderBrush,
            HoverBrush,
            BrightWeakTextBrush,
            TextBrush,
            WeakTextBrush,
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper(),
            new EdgeCapsulePreviewThemeResources(
                Theme.LinkBrush,
                CheckBoxBorderBrush,
                Theme.ActiveBrush,
                Theme.CheckBoxHoverBorderBrush,
                Theme.CheckBoxUncheckedHoverBgBrush,
                Theme.CheckBoxActiveHoverBrush));
    }

    public void UpdateEdgeCapsuleCloseButtonMode()
    {
        if (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement)
        {
            return;
        }

        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.State,
            refreshLayout: true);
    }

    private void RequestEdgeCapsulePresentation(
        bool animate,
        EdgeCapsuleTransitionReason reason,
        int durationMilliseconds = EdgeCapsuleLayout.SlotMoveMilliseconds,
        bool refreshLayout = false)
    {
        animate = animate && _controller.State.EnableAnimations;
        _edgeCapsule.RequestPresentation(animate
            ? EdgeCapsuleMotion.Animate(reason, durationMilliseconds)
            : EdgeCapsuleMotion.Snap(reason));

        // State-driven requests are allowed to depend on controller settings captured in the
        // layout snapshot (resting opacity, close-button behavior, sizing facts, etc.). Reusing
        // the previous snapshot makes a settings change appear only after the next pointer or
        // placement invalidation. Always recapture those facts for an explicit state refresh.
        if (reason == EdgeCapsuleTransitionReason.State)
        {
            refreshLayout = true;
        }

        var dirty = EdgeCapsuleDirty.Presentation;
        if (refreshLayout)
        {
            dirty |= EdgeCapsuleDirty.Measure;
        }
        InvalidateEdgeCapsule(dirty);
    }

    private void FlushEdgeCapsulePresentation(
        EdgeCapsuleTransitionReason reason,
        EdgeCapsuleDirty dirty = EdgeCapsuleDirty.Presentation)
    {
        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Snap(reason));
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Flush(dirty, dispatcher, ReconcileEdgeCapsule);
    }

    internal void InvalidateEdgeCapsuleDisplayMetrics()
    {
        if (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement)
        {
            return;
        }

        _edgeCapsuleHost?.InvalidateNativeMetrics();
        _edgeCapsule.ForceApplyCurrentPresentation();
        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Preserve(
            EdgeCapsuleTransitionReason.DisplayMetrics));
        InvalidateEdgeCapsule(
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure |
            EdgeCapsuleDirty.DisplayMetrics);
    }

    private EdgeCapsuleLayoutSnapshot CaptureEdgeCapsuleLayoutSnapshot()
    {
        var monitor = DeepCapsuleMonitorGeometry();
        var restingWidth = DeepCapsuleVisibleWidth(monitor.DpiScaleY);
        var previewSize = CurrentEdgeCapsulePreviewSize;
        var previewWidth = previewSize?.WidthDip ??
            restingWidth + CapsuleCloseWidth;
        var previewHeight = previewSize?.HeightDip ??
            PaperLayoutDefaults.CapsuleHeight;
        var requestedHostWidth = Math.Max(
            Math.Max(
                restingWidth + CapsuleCloseWidth,
                previewWidth),
            Math.Min(
                EdgeCapsuleLayout.HostCapacityWidth,
                monitor.LocalWorkAreaDip.Width));
        var applied = _edgeCapsule.AppliedPresentation;
        var appliedHostWidth = applied.Visible && !applied.HostBounds.IsEmpty
            ? applied.HostBounds.Width / Math.Max(1, applied.DpiScaleX)
            : 0;
        var appliedHostHeight = applied.Visible && !applied.HostBounds.IsEmpty
            ? applied.HostBounds.Height / Math.Max(1, applied.DpiScaleY)
            : 0;

        // HostBounds is transparent composition capacity, not the current card. It may grow before
        // a larger preview starts, but it does not contract while this host remains visible. Opening
        // and closing therefore resize only VisualSurface; queue placement may still move the HWND.
        var hostWidth = Math.Max(requestedHostWidth, appliedHostWidth);
        var hostHeight = Math.Max(
            Math.Max(PaperLayoutDefaults.CapsuleHeight, previewHeight),
            appliedHostHeight);
        var restingOpacity = _controller.State.ExperimentalRestingCapsuleOpacity
            ? ExperimentalOpacityLevels.Normalize(
                _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                ExperimentalOpacityLevels.DefaultRestingCapsule)
            : 1.0;
        double? forcedOpacity = _controller.IsAdvancedCapsuleTransparent(_paper)
            ? _controller.AdvancedShortcutOpacity
            : _controller.State.ExperimentalRestingCapsuleOpacity &&
              _controller.State.ExperimentalRestingCapsuleOpacityAlways
                ? restingOpacity
                : null;
        return EdgeCapsuleLayoutService.Calculate(new EdgeCapsuleLayoutFacts(
            monitor,
            MyDeepCapsuleEdge,
            _edgeCapsule.Placement,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            DeepCapsuleGap,
            restingWidth,
            CapsuleCloseWidth,
            hostWidth,
            hostHeight,
            PaperLayoutDefaults.CapsuleHeight,
            previewWidth,
            previewHeight,
            _controller.State.HideEdgeCapsuleCloseButtonOnHover,
            restingOpacity,
            forcedOpacity));
    }

    private bool ApplyEdgeCapsulePresentationFrame(EdgeCapsulePresentationFrame frame)
    {
        if (!frame.Visible)
        {
            return _edgeCapsuleHost?.Apply(frame) ?? true;
        }

        EnsureDeepCapsuleSlotHost();
        return _edgeCapsuleHost?.Apply(frame) == true;
    }

    private DeviceScreenPoint? CaptureEdgeCapsulePointerPosition()
    {
        return WindowNative.TryGetCursorScreenPosition(out var pointer)
            ? pointer
            : null;
    }

    private void InvalidateEdgeCapsulePointer() =>
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Pointer);

    private void InvalidateEdgeCapsulePointerFromHostInput(
        DeviceScreenPoint? authoritativePointer = null)
    {
        _controller.InvalidateEdgeCapsulePreviewPointerResolution();
        _controller.NotifyEdgeCapsulePreviewPhysicalPointer(
            this,
            authoritativePointer ?? CaptureEdgeCapsulePointerPosition());
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.InvalidateBeforeNextRender(
            EdgeCapsuleDirty.Pointer,
            dispatcher,
            ReconcileEdgeCapsule);
    }

    private void ScheduleDeepCapsuleSlotMeasureRefresh()
    {
        if (_edgeCapsuleHost?.IsVisible == true && HasDeepCapsuleSlotPlacement)
        {
            InvalidateEdgeCapsule(EdgeCapsuleDirty.Measure);
        }
    }

    private void InvalidateEdgeCapsule(EdgeCapsuleDirty dirty)
    {
        if ((dirty & EdgeCapsuleDirty.Pointer) != 0)
        {
            _controller.InvalidateEdgeCapsulePreviewPointerResolution();
        }
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Invalidate(dirty, dispatcher, ReconcileEdgeCapsule);
    }

    private EdgeCapsuleDirty ReconcileEdgeCapsule(EdgeCapsuleDirty dirty)
    {
        var wasRetracting = IsDeepCapsuleSlotRetracting;
        var wasPointerOver = _edgeCapsule.PointerOverSurface;
        var appliedPresentationVersion =
            _edgeCapsule.AppliedPresentationVersion;
        var remaining = _edgeCapsule.Reconcile(
            dirty,
            CaptureEdgeCapsuleLayoutSnapshot,
            CaptureEdgeCapsulePointerPosition,
            ApplyEdgeCapsulePresentationFrame);
        var pointer = _edgeCapsule.LastPointerSample;
        if (!_edgeCapsuleReconcileNotificationPending)
        {
            _edgeCapsulePendingPointerOverBaseline = wasPointerOver;
        }
        _edgeCapsuleReconcileNotificationPending = true;
        _edgeCapsulePresentationChangePending |= appliedPresentationVersion !=
            _edgeCapsule.AppliedPresentationVersion;
        _edgeCapsulePendingPointerOver = _edgeCapsule.PointerOverSurface;
        _edgeCapsulePointerOverChangePending =
            _edgeCapsulePendingPointerOverBaseline !=
            _edgeCapsulePendingPointerOver;
        _edgeCapsulePendingPointerSample = pointer;
        if (wasRetracting && !IsDeepCapsuleSlotRetracting)
        {
            UpdateDeepCapsuleSlotHostTheme();
            UpdateCapsuleClosePlacement();
        }

        // Shared-frame Apply updates every presenter's immutable frame before the native HWND batch
        // is committed. Delay controller observers so their queue-wide scans cannot see a mixture
        // of this frame and the previous one. A normal reconcile has no shared batch and preserves
        // the existing immediate notification behavior.
        if (!_edgeCapsuleVisualTransactionNotificationDeferred &&
            !_edgeCapsule.TryDeferSharedFramePostCommit(
                PublishEdgeCapsuleReconcileNotifications) &&
            (remaining & EdgeCapsuleDirty.ApplyRetry) == 0)
        {
            PublishEdgeCapsuleReconcileNotifications();
        }
        return remaining;
    }

    private void PublishEdgeCapsuleReconcileNotifications()
    {
        if (!_edgeCapsuleReconcileNotificationPending)
        {
            return;
        }

        var presentationChanged = _edgeCapsulePresentationChangePending;
        var pointerOverChanged = _edgeCapsulePointerOverChangePending;
        var pointerOver = _edgeCapsulePendingPointerOver;
        var pointer = _edgeCapsulePendingPointerSample;
        _edgeCapsuleReconcileNotificationPending = false;
        _edgeCapsulePresentationChangePending = false;
        _edgeCapsulePointerOverChangePending = false;

        if (presentationChanged)
        {
            _controller.InvalidateEdgeCapsulePreviewPointerResolution();
        }
        if (pointerOverChanged)
        {
            _controller.NotifyEdgeCapsulePointerOverChanged(this, pointerOver);
        }
        _controller.NotifyEdgeCapsulePreviewPhysicalPointer(this, pointer);
    }

    internal void PublishEdgeCapsuleVisualTransactionNotifications() =>
        PublishEdgeCapsuleReconcileNotifications();

    private void CloseExpandedDeepCapsuleSlotHostForReal()
    {
        CancelDeepCapsuleReorderDrag();
        CloseDeepCapsuleSlotContextMenu();
        ClearEdgeCapsulePreviewContent();
        _edgeCapsule.Reset();
        _edgeCapsuleHost?.Dispose();
        _edgeCapsuleHost = null;
    }

    // ── This window's OWN queue identity. A queue is (monitor, edge); each docked capsule
    // resolves its geometry against its own queue, not a single global anchor. This is what
    // lets one capsule sit on the left edge of monitor A while another sits on the right of B.
    private EdgeCapsuleEdge MyDeepCapsuleEdge =>
        _paper.CapsuleSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right;

    private bool MyDeepCapsuleIsLeftEdge => MyDeepCapsuleEdge == EdgeCapsuleEdge.Left;

    private Rect DeepCapsuleWorkArea()
    {
        return EdgeCapsuleLayout.WorkAreaForQueue(_paper.CapsuleMonitorDeviceName);
    }

    private MonitorGeometry DeepCapsuleMonitorGeometry()
    {
        if (_edgeCapsuleHost?.TryGetMonitorGeometry(
                _paper.CapsuleMonitorDeviceName,
                out var geometry) == true ||
            _edgeCapsuleHost == null && WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _paper.CapsuleMonitorDeviceName,
                out geometry))
        {
            return geometry;
        }

        var dpi = _edgeCapsuleHost?.Dpi ?? VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;
        return new MonitorGeometry(
            "",
            new DeviceScreenRect(
                (int)Math.Round(area.Left * dpi.DpiScaleX),
                (int)Math.Round(area.Top * dpi.DpiScaleY),
                (int)Math.Round(area.Right * dpi.DpiScaleX),
                (int)Math.Round(area.Bottom * dpi.DpiScaleY)),
            Math.Max(1, dpi.DpiScaleX),
            Math.Max(1, dpi.DpiScaleY));
    }

    private DpiScale DeepCapsuleSlotDpi()
    {
        var geometry = DeepCapsuleMonitorGeometry();
        return new DpiScale(geometry.DpiScaleX, geometry.DpiScaleY);
    }

    private double MyTopForIndex(int index, int slotCount)
    {
        return EdgeCapsuleLayoutService.TopForVisualIndex(
            DeepCapsuleMonitorGeometry(),
            index,
            slotCount,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            DeepCapsuleGap);
    }

}
