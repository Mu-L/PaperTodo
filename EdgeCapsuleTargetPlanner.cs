namespace PaperTodo;

/// <summary>
/// Pure desired-model to shape/layout planner. FloatingFree is a first-class shape and therefore
/// cannot inherit the docked wall-side close segment from a constructor parameter.
/// </summary>
internal static class EdgeCapsuleTargetPlanner
{
    public static EdgeCapsulePresentationPlan Calculate(
        EdgeCapsuleModel model,
        EdgeCapsuleLayoutSnapshot layout)
    {
        if (model.State.Slot == EdgeCapsuleSlotState.None ||
            !model.Placement.IsPlaced ||
            !layout.IsUsable)
        {
            return EdgeCapsulePresentationPlan.Hidden;
        }

        var retracted = model.State.Slot is
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractedExpanded or
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded;
        var retracting = model.State.Slot is
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded;
        var ownsFloatingHost = model.State.Gesture is
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering or
            EdgeCapsuleGestureState.DockingHandoff or
            EdgeCapsuleGestureState.DockingReveal;
        var dockedSuppressed = model.State.Gesture is
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering or
            EdgeCapsuleGestureState.DockingHandoff;
        var preview = !retracted &&
            !dockedSuppressed &&
            model.Preview == EdgeCapsulePreviewState.Open;
        // The detached floating HWND owns every visible drag pixel. Keep the suppressed permanent
        // docked endpoint compact so Hovered/Active cannot continue hidden width mutations behind
        // that cover.
        var expanded = !ownsFloatingHost &&
            !preview &&
            !retracted &&
            (model.State.Visual is
                EdgeCapsuleVisualState.Hovered or
                EdgeCapsuleVisualState.Active);
        var top = model.DockedDragTopDipOverride ??
            (retracted ? layout.MasterTopDip : layout.NormalTopDip);
        var closeWidth =
            preview || expanded
                ? layout.MaximumCloseWidthDip
                : 0;
        var visibleHeight = preview
            ? layout.PreviewHeightDip
            : layout.HeightDip;
        var bodyWidth = preview
            ? Math.Max(1, layout.PreviewWidthDip - closeWidth)
            : layout.RestingWidthDip;
        var geometry = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            layout.Monitor,
            layout.Edge,
            top,
            bodyWidth,
            closeWidth,
            visibleHeight));
        // V3 Lite keeps one stable, local HWND capacity for this paper. It is bounded by the
        // documented maximum preview size and never spans the queue or work area. WPF owns every
        // Resting/Hover/Preview morph inside this capacity; DComp may only translate the capacity.
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
        var hostBodyWidth = Math.Max(
            layout.RestingWidthDip,
            maximumPreview.WidthDip - layout.MaximumCloseWidthDip);
        var hostGeometry = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                top,
                hostBodyWidth,
                layout.MaximumCloseWidthDip,
                Math.Max(layout.HeightDip, maximumPreview.HeightDip)));
        var surface = SurfaceFor(
            model,
            preview,
            retracted,
            retracting,
            dockedSuppressed);
        var hitTest = !retracted && !ownsFloatingHost;
        var interactiveBounds = hitTest ? geometry.InteractiveBounds : default;
        var contentOpacity = layout.ForcedContentOpacity is { } forcedOpacity
            ? Math.Clamp(forcedOpacity, 0, 1)
            : surface == EdgeCapsuleSurfaceKind.DockedResting
                ? Math.Clamp(layout.RestingContentOpacity, 0, 1)
                : 1;
        var docked = new EdgeCapsuleTargetPresentation(
            true,
            surface,
            geometry.Bounds,
            hostGeometry.Bounds,
            interactiveBounds,
            layout.Edge,
            geometry.RestingWidthDevice,
            geometry.WallDeviceX,
            geometry.DpiScaleX,
            geometry.DpiScaleY,
            layout.MaximumCloseWidthDip,
            retracted ? 0 : 1,
            dockedSuppressed ? 0 : contentOpacity,
            !retracted && !dockedSuppressed &&
                model.State.Visual == EdgeCapsuleVisualState.Active,
            hitTest,
            preview ? false : layout.CloseSegmentActsAsContent);

        var floatingShape = ownsFloatingHost
            ? CreateFloatingShape(layout, model.State.Visual == EdgeCapsuleVisualState.Active)
            : EdgeCapsuleFloatingShape.Hidden;
        return new EdgeCapsulePresentationPlan(docked, floatingShape);
    }

    private static EdgeCapsuleSurfaceKind SurfaceFor(
        EdgeCapsuleModel model,
        bool preview,
        bool retracted,
        bool retracting,
        bool dockedSuppressed)
    {
        if (dockedSuppressed)
        {
            return EdgeCapsuleSurfaceKind.DockedSuppressed;
        }
        if (retracting)
        {
            return EdgeCapsuleSurfaceKind.DockedRetracting;
        }
        if (retracted)
        {
            return EdgeCapsuleSurfaceKind.DockedRetracted;
        }
        if (preview)
        {
            return EdgeCapsuleSurfaceKind.DockedPreview;
        }
        return model.State.Visual switch
        {
            EdgeCapsuleVisualState.Active => EdgeCapsuleSurfaceKind.DockedActive,
            EdgeCapsuleVisualState.Hovered => EdgeCapsuleSurfaceKind.DockedHovered,
            _ => EdgeCapsuleSurfaceKind.DockedResting
        };
    }

    internal static EdgeCapsuleFloatingShape CreateFloatingShape(
        EdgeCapsuleLayoutSnapshot layout,
        bool outlineVisible)
    {
        var height = layout.HeightDip;
        var bodyHeight = Math.Max(1, height - EdgeCapsuleLayout.WindowChromeMargin * 2);
        return new EdgeCapsuleFloatingShape(
            true,
            EdgeCapsuleSurfaceKind.FloatingFree,
            Math.Max(
                PaperLayoutDefaults.CapsuleWidth,
                layout.RestingWidthDip + EdgeCapsuleLayout.WindowChromeMargin),
            height,
            bodyHeight,
            Math.Min(EdgeCapsuleLayout.CornerRadius, bodyHeight / 2),
            outlineVisible);
    }
}
