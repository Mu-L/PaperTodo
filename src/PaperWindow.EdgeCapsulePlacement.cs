namespace PaperTodo;

public sealed partial class PaperWindow
{
    private double DeepCapsuleTopForIndex(int index)
    {
        return MyTopForIndex(index, _edgeCapsule.Placement.SlotCount);
    }

    private double DeepCapsuleVisibleWidth()
    {
        return DeepCapsuleVisibleWidth(DeepCapsuleSlotDpi().PixelsPerDip);
    }

    private double DeepCapsuleVisibleWidth(double pixelsPerDip)
    {
        var pluginContentWidth = PluginCapsuleRequestedContentWidth(pixelsPerDip);
        if (pluginContentWidth.HasValue)
        {
            return Math.Max(34, Math.Ceiling(pluginContentWidth.Value + WindowChromeMargin));
        }

        // A resting edge tag owns exactly the pixels it renders: one interior shadow margin plus
        // icon/title content and its padding. There is no hidden full-width pill behind it.
        var bodyWidth = Math.Ceiling(
            CapsuleLeftPadding +
            MeasureCapsuleIconWidth(pixelsPerDip) +
            CapsuleIconGap +
            MeasureCapsuleTitleWidth(
                limitForDeepCapsule: true,
                pixelsPerDip: pixelsPerDip) +
            CapsuleRightPadding);
        return Math.Max(34, bodyWidth + WindowChromeMargin);
    }

    private double ExpandedDeepCapsuleVisibleWidth()
    {
        return DeepCapsuleVisibleWidth() + CapsuleCloseWidth;
    }

    // Slide this capsule up to the master's slot and fade it out. The window stays shown
    // (so it keeps counting as a deep-capsule member) but, being a per-pixel transparent
    // window at Opacity 0, it is fully click-through and never blocks the master pill.
    internal void RetractIntoMaster(EdgeCapsulePlacement placement, bool animate)
    {
        TraceCollapseAllPaper(
            "paper-retract-enter",
            placement,
            animate,
            "route=unresolved");

        if (!_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible ||
            !_controller.CanPaperDisplayAsCapsule(_paper))
        {
            TraceCollapseAllPaper(
                "paper-retract-abort",
                placement,
                animate,
                "reason=eligibility");
            ClearDeepCapsulePlacement();
            return;
        }

        if (!AttachEdgeCapsuleToQueue(
                placement,
                _paper.IsCollapsed ? EdgeCapsulePaperForm.Collapsed : EdgeCapsulePaperForm.Expanded,
                retracted: true))
        {
            TraceCollapseAllPaper(
                "paper-retract-abort",
                placement,
                animate,
                "reason=attach-rejected");
            return;
        }
        TraceCollapseAllPaper(
            "paper-retract-attached",
            placement,
            animate,
            "modelTarget=retracted");
        UpdateDeepCapsuleSlotHostTheme();
        RefreshEffectiveTopmost();

        var staged = TryStageEdgeCapsuleVisualTransaction(
            animate,
            EdgeCapsuleTransitionReason.Retraction,
            EdgeCapsuleLayout.SlotRetractMoveMilliseconds,
            refreshLayout: true);
        TraceCollapseAllPaper(
            "paper-retract-route",
            placement,
            animate,
            $"staged={staged} durationMs={EdgeCapsuleLayout.SlotRetractMoveMilliseconds}");
        if (!staged)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.Retraction,
                EdgeCapsuleLayout.SlotRetractMoveMilliseconds,
                refreshLayout: true);
            TraceCollapseAllPaper(
                "paper-retract-requested",
                placement,
                animate,
                "route=direct-presenter");
        }
        if (_paper.IsCollapsed)
        {
            HideMainWindowForDeepCapsuleRest();
        }
        TraceCollapseAllPaper(
            "paper-retract-exit",
            placement,
            animate,
            $"hostVisible={_edgeCapsuleHost?.IsVisible == true}");
    }

    internal void ApplyDeepCapsulePlacement(EdgeCapsulePlacement placement, bool animate = false)
    {
        var releasingFromMaster =
            IsDeepCapsuleRetractedIntoMaster ||
            IsDeepCapsuleSlotRetracting ||
            _edgeCapsule.AppliedPresentation.Surface is
                EdgeCapsuleSurfaceKind.DockedRetracted or
                EdgeCapsuleSurfaceKind.DockedRetracting;
        if (releasingFromMaster)
        {
            TraceCollapseAllPaper(
                "paper-release-enter",
                placement,
                animate,
                "route=unresolved");
        }

        if (!_paper.IsCollapsed || !_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            if (releasingFromMaster)
            {
                TraceCollapseAllPaper(
                    "paper-release-abort",
                    placement,
                    animate,
                    "reason=eligibility");
            }
            ClearDeepCapsulePlacement();
            return;
        }

        if (!AttachEdgeCapsuleToQueue(
                placement,
                EdgeCapsulePaperForm.Collapsed,
                retracted: false))
        {
            if (releasingFromMaster)
            {
                TraceCollapseAllPaper(
                    "paper-release-abort",
                    placement,
                    animate,
                    "reason=attach-rejected");
            }
            return;
        }
        if (releasingFromMaster)
        {
            TraceCollapseAllPaper(
                "paper-release-attached",
                placement,
                animate,
                "modelTarget=docked");
        }
        RefreshCapsuleLabel();
        ReserveEdgeCapsulePreviewCapacityBeforeFirstShow();
        QueueDeepCapsuleFloatingDragInfrastructurePrewarm(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            requireActiveInteraction: false);
        var staged = TryStageEdgeCapsuleVisualTransaction(
            animate,
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
        if (releasingFromMaster)
        {
            TraceCollapseAllPaper(
                "paper-release-route",
                placement,
                animate,
                $"staged={staged} durationMs={EdgeCapsuleLayout.SlotMoveMilliseconds}");
        }
        if (!staged)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true);
            if (releasingFromMaster)
            {
                TraceCollapseAllPaper(
                    "paper-release-requested",
                    placement,
                    animate,
                    "route=direct-presenter");
            }
        }
        if (!IsPaperFormTransitioning)
        {
            HideMainWindowForDeepCapsuleRest();
        }
        RefreshEffectiveTopmost();
        ScheduleMigratedPluginBodyPreviewWarmup();
        if (releasingFromMaster)
        {
            TraceCollapseAllPaper(
                "paper-release-exit",
                placement,
                animate,
                $"hostVisible={_edgeCapsuleHost?.IsVisible == true}");
        }
    }

    internal void PreviewDeepCapsulePlacement(EdgeCapsulePlacement placement)
    {
        if (!HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost?.IsVisible != true ||
            IsDeepCapsuleReordering ||
            IsDeepCapsuleRetractedIntoMaster)
        {
            return;
        }

        if (!UpdateEdgeCapsuleQueuePlacement(placement))
        {
            return;
        }
        if (!TryStageEdgeCapsuleVisualTransaction(
                animate: true,
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true))
        {
            RequestEdgeCapsulePresentation(
                animate: true,
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true);
        }
    }

    internal void ApplyExpandedDeepCapsuleSlotPlacement(
        EdgeCapsulePlacement placement,
        bool animate = false,
        bool deferInitialPresentation = false)
    {
        var shouldReserveWhileExpanded = _controller.State.ShowDeepCapsuleWhileExpanded &&
            _controller.CanPaperDisplayAsCapsule(_paper);
        if (_paper.IsCollapsed ||
            !shouldReserveWhileExpanded ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible)
        {
            ClearExpandedDeepCapsuleSlotPlacement();
            return;
        }

        var shouldSaveExpandedGeometry = ShouldSaveDeepCapsuleExpandedGeometry;
        if (!AttachEdgeCapsuleToQueue(
                placement,
                EdgeCapsulePaperForm.Expanded,
                retracted: false))
        {
            return;
        }
        MarkEdgeCapsuleOpenedFromEdge();
        RefreshCapsuleLabel();
        ReserveEdgeCapsulePreviewCapacityBeforeFirstShow();
        QueueDeepCapsuleFloatingDragInfrastructurePrewarm(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            requireActiveInteraction: false);
        UpdateDeepCapsuleSlotHostTheme();

        RefreshDeepCapsuleSlotLabel();

        var firstShow = _edgeCapsuleHost?.IsVisible != true;
        if (TryStageEdgeCapsuleVisualTransaction(
                animate,
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true))
        {
            // The transaction commit owns the first visible frame together with every sibling.
        }
        else if (firstShow && !deferInitialPresentation)
        {
            FlushEdgeCapsulePresentation(
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
        }
        else
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true);
        }
        RefreshEffectiveTopmost();
        UpdateToolTipSetting();
        if (!IsPaperFormTransitioning && shouldSaveExpandedGeometry)
        {
            _controller.UpdateGeometry(_paper, this);
        }
    }

    internal void FlushStartupDeepCapsulePresentation()
    {
        if (!HasDeepCapsuleSlotPlacement)
        {
            return;
        }

        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
    }

    public void ClearExpandedDeepCapsuleSlotPlacement(bool animate = false)
    {
        ChangeEdgeCapsulePaperForm(
            _paper.IsCollapsed ? EdgeCapsulePaperForm.Collapsed : EdgeCapsulePaperForm.Expanded,
            reserveWhileExpanded: false);
        UpdateDeepCapsuleSlotHostTheme();
        if (_paper.IsCollapsed && HasDeepCapsuleSlotPlacement)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.State,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
        }
    }

    private void HideExpandedDeepCapsuleSlotHost(bool animate)
    {
        animate = animate && _controller.State.EnableAnimations;
        if (animate &&
            _edgeCapsuleHost?.IsVisible == true &&
            HasDeepCapsuleSlotPlacement &&
            _edgeCapsule.Placement.IsPlaced &&
            !IsDeepCapsuleSlotRetracting)
        {
            if (!BeginEdgeCapsuleRetraction())
            {
                return;
            }
            RequestEdgeCapsulePresentation(
                animate: true,
                EdgeCapsuleTransitionReason.Retraction,
                EdgeCapsuleLayout.SlotRetractMoveMilliseconds);
            return;
        }

        DetachEdgeCapsuleFromQueue();
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.Retraction);
    }

    public void ClearDeepCapsulePlacement(bool animate = false)
    {
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        CancelDeepCapsuleReorderDrag();
        RestorePrewarmedPluginBodyForActivation("placement-cleared");
        animate = animate && _controller.State.EnableAnimations;

        var shouldRetractBeforeHide = animate &&
            _edgeCapsuleHost?.IsVisible == true &&
            HasDeepCapsuleSlotPlacement &&
            !IsDeepCapsuleRetractedIntoMaster;

        if (shouldRetractBeforeHide)
        {
            HideExpandedDeepCapsuleSlotHost(animate: true);
        }
        else
        {
            UpdateCapsuleClosePlacement();
            HideExpandedDeepCapsuleSlotHost(animate);
        }

        // A capsule may have been faded out while retracted behind the master; never leave
        // a live (expanded or free-floating) window invisible.
        if (Math.Abs(Opacity - 1.0) > 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearEdgeCapsuleOpenOrigin();
        }
    }

    // Fully remove this window from the edge stack, including any expanded reservation, and hide
    // the docked host. Controller code uses this single operation when a paper leaves the stack.
    public void DetachFromDeepCapsuleStack(bool animate = false)
    {
        ClearDeepCapsulePlacement(animate: animate);
    }

    public void UpdateDeepCapsuleMode()
    {
        if (!_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
        }
        else if (!_paper.IsCollapsed)
        {
            ClearDeepCapsulePlacement();
        }
        else
        {
            RequestEdgeCapsulePresentation(
                animate: false,
                EdgeCapsuleTransitionReason.State);
        }

        RefreshEffectiveTopmost();
    }

    public void UpdateDeepCapsuleExpandedSlotMode()
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            DetachEdgeCapsuleFromQueue();
            return;
        }

        if (_controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper))
        {
            if (!ChangeEdgeCapsulePaperForm(
                    EdgeCapsulePaperForm.Expanded,
                    reserveWhileExpanded: true))
            {
                return;
            }
            RefreshCapsuleLabel();
            UpdateDeepCapsuleSlotHostTheme();
            RequestEdgeCapsulePresentation(
                animate: _controller.State.EnableAnimations,
                EdgeCapsuleTransitionReason.State);
            return;
        }

        if (!_controller.State.ShowDeepCapsuleWhileExpanded && HoldsDeepCapsuleSlotWhileExpanded)
        {
            ClearDeepCapsulePlacement(animate: _controller.State.EnableAnimations);
        }
    }

    private void TraceCollapseAllPaper(
        string phase,
        EdgeCapsulePlacement placement,
        bool animate,
        string extra)
    {
#if DEBUG
        if (!EdgeCapsuleRetractionDiagnostics.IsActive)
        {
            return;
        }
        var applied = _edgeCapsule.AppliedPresentation;
        var modelPlacement = _edgeCapsule.Placement;
        EdgeCapsuleRetractionDiagnostics.Trace(
            phase,
            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"monitor={_paper.CapsuleMonitorDeviceName} side={_paper.CapsuleSide} " +
            $"animateRequested={animate} animationsEnabled={_controller.State.EnableAnimations} " +
            $"slot={EdgeCapsuleSlot} visual={EdgeCapsuleVisual} gesture={EdgeCapsuleGesture} " +
            $"preview={_edgeCapsule.Preview} pointerOver={_edgeCapsule.PointerOverSurface} " +
            $"placement={placement.Index}/{placement.VisualOffset}/{placement.SlotCount}/" +
            $"{placement.TopOffsetDip:F1} modelPlacement={modelPlacement.Index}/" +
            $"{modelPlacement.VisualOffset}/{modelPlacement.SlotCount}/{modelPlacement.TopOffsetDip:F1} " +
            $"appliedSurface={applied.Surface} appliedTop={applied.Bounds.Top} " +
            $"appliedHostTop={applied.HostBounds.Top} opacity={applied.Opacity:F4} " +
            $"contentOpacity={applied.ContentOpacity:F4} hit={applied.IsHitTestVisible} " +
            $"activeTransition={_edgeCapsule.HasActiveTransition} {extra}");
#endif
    }
}
