using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace PaperTodo;

/// <summary>
/// Branch-only adapter for the WPF-native Resting <-> Hovered experiment. The production Host.Apply
/// path remains untouched: this overload supplies the exact planner endpoint only when a new native
/// WPF clock is started, and prevents the legacy sampled-frame tail from settling that clock early.
/// </summary>
internal sealed partial class EdgeCapsuleHost
{
    private const double WpfNativeHoverSettleSuppressionWidthDip = 1_000_000;

    public bool Apply(
        EdgeCapsulePresentationFrame frame,
        Func<EdgeCapsulePresentationFrame> targetFrameProvider)
    {
        ArgumentNullException.ThrowIfNull(targetFrameProvider);

        var generationBeforeApply = _wpfNativeHoverAnimationGeneration;
        var suppressLogicalEndpointSettle =
            _wpfNativeHoverGeometryOwned &&
            _wpfNativeHoverAnimationRunning;
        var savedTargetWidthDip = _wpfNativeHoverTargetWidthDip;
        var savedTargetCloseWidthDip = _wpfNativeHoverTargetCloseWidthDip;

        if (suppressLogicalEndpointSettle)
        {
            // The old Presenter sampler reaches the final device pixel well before its 160 ms
            // timing interval ends. Keep that sampled endpoint from calling Settle while WPF's own
            // animation clock still owns the visible geometry. The temporary values are never used
            // by the animation object itself and are restored before returning to the Dispatcher.
            _wpfNativeHoverTargetWidthDip = WpfNativeHoverSettleSuppressionWidthDip;
            _wpfNativeHoverTargetCloseWidthDip = WpfNativeHoverSettleSuppressionWidthDip;
        }

        bool applied;
        try
        {
            applied = Apply(frame);
        }
        finally
        {
            if (suppressLogicalEndpointSettle &&
                _wpfNativeHoverGeometryOwned &&
                generationBeforeApply == _wpfNativeHoverAnimationGeneration)
            {
                _wpfNativeHoverTargetWidthDip = savedTargetWidthDip;
                _wpfNativeHoverTargetCloseWidthDip = savedTargetCloseWidthDip;
            }
        }

        if (!applied ||
            !_wpfNativeHoverGeometryOwned ||
            generationBeforeApply == _wpfNativeHoverAnimationGeneration)
        {
            return applied;
        }

        // A generation change while ownership survived means the normal Host path just started or
        // retargeted a Resting <-> Hovered clock. Resolve the planner endpoint once at that boundary;
        // do not recalculate it on every sampled Presenter frame.
        var targetFrame = targetFrameProvider();
        if (!CanUseExactWpfNativeHoverTarget(targetFrame))
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"wpf.native-hover phase=endpoint-rejected paper={_options.DiagnosticId} " +
                $"sample={frame.Surface} target={targetFrame.Surface}");
#endif
            return applied;
        }

        ArmExactWpfNativeHoverEndpoint(frame, targetFrame);
        return applied;
    }

    private bool CanUseExactWpfNativeHoverTarget(
        EdgeCapsulePresentationFrame targetFrame)
    {
        var anchor = _wpfNativeHoverAnchorFrame;
        return targetFrame.Visible &&
            targetFrame.IsUsable &&
            targetFrame.Surface == _wpfNativeHoverTargetSurface &&
            IsWpfNativeHoverSurface(targetFrame.Surface) &&
            targetFrame.HostBounds == anchor.HostBounds &&
            targetFrame.Edge == anchor.Edge &&
            targetFrame.WallDeviceX == anchor.WallDeviceX &&
            targetFrame.Bounds.Top == anchor.Bounds.Top &&
            targetFrame.Bounds.Height == anchor.Bounds.Height &&
            targetFrame.BodyWindowWidthDevice == anchor.BodyWindowWidthDevice &&
            Math.Abs(targetFrame.DpiScaleX - anchor.DpiScaleX) < 0.001 &&
            Math.Abs(targetFrame.DpiScaleY - anchor.DpiScaleY) < 0.001 &&
            Math.Abs(
                targetFrame.MaximumCloseWidthDip -
                anchor.MaximumCloseWidthDip) < 0.001 &&
            targetFrame.CloseSegmentActsAsContent ==
                anchor.CloseSegmentActsAsContent;
    }

    private void ArmExactWpfNativeHoverEndpoint(
        EdgeCapsulePresentationFrame sampledFrame,
        EdgeCapsulePresentationFrame targetFrame)
    {
        var dpiScaleX = Math.Max(1, targetFrame.DpiScaleX);
        var closeColumn = WpfNativeHoverCloseColumn(targetFrame.Edge);
        var currentWidth =
            double.IsFinite(VisualSurface.ActualWidth) &&
            VisualSurface.ActualWidth > 0
                ? VisualSurface.ActualWidth
                : sampledFrame.Bounds.Width /
                    Math.Max(1, sampledFrame.DpiScaleX);
        var currentCloseWidth = double.IsFinite(closeColumn.ActualWidth)
            ? Math.Max(0, closeColumn.ActualWidth)
            : EdgeCapsuleGeometry.CloseWidthForAppliedDeviceWidth(
                sampledFrame.Bounds.Width,
                sampledFrame.BodyWindowWidthDevice,
                sampledFrame.DpiScaleX,
                sampledFrame.MaximumCloseWidthDip);
        var currentCloseOpacity = Math.Clamp(CloseArea.Opacity, 0, 1);
        var targetWidth = targetFrame.Bounds.Width / dpiScaleX;
        var targetCloseWidth =
            EdgeCapsuleGeometry.CloseWidthForAppliedDeviceWidth(
                targetFrame.Bounds.Width,
                targetFrame.BodyWindowWidthDevice,
                targetFrame.DpiScaleX,
                targetFrame.MaximumCloseWidthDip);
        var targetCloseOpacity = targetFrame.CloseSegmentActsAsContent
            ? 1
            : targetFrame.MaximumCloseWidthDip <= 0
                ? 0
                : Math.Clamp(
                    targetCloseWidth /
                        targetFrame.MaximumCloseWidthDip,
                    0,
                    1);

        // Invalidate the completion callback installed by the provisional Host clock, then replace
        // it before WPF gets another render pass. The user therefore sees one continuous animation
        // from the current visual value to the exact device-pixel endpoint, never the provisional
        // BodyWindowWidthDevice / DPI + MaximumCloseWidthDip estimate.
        ++_wpfNativeHoverAnimationGeneration;
        ClearWpfNativeHoverAnimations(targetFrame.Edge);
        _wpfNativeHoverTargetWidthDip = targetWidth;
        _wpfNativeHoverTargetCloseWidthDip = targetCloseWidth;
        _wpfNativeHoverTargetCloseOpacity = targetCloseOpacity;
        _wpfNativeHoverAnimationRunning = true;
        var generation = _wpfNativeHoverAnimationGeneration;

        VisualSurface.HorizontalAlignment = targetFrame.Edge == EdgeCapsuleEdge.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        VisualSurface.Width = targetWidth;
        VisualSurface.Height = targetFrame.Bounds.Height /
            Math.Max(1, targetFrame.DpiScaleY);
        VisualSurfaceOffset.X = 0;
        VisualSurfaceOffset.Y = 0;
        closeColumn.Width = new GridLength(targetFrame.MaximumCloseWidthDip);
        closeColumn.MaxWidth = double.PositiveInfinity;
        CloseArea.Width = double.NaN;
        CloseArea.Opacity = targetCloseOpacity;

        var duration = new Duration(TimeSpan.FromMilliseconds(
            EdgeCapsuleLayout.HorizontalResizeMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var widthAnimation = new DoubleAnimation(
            currentWidth,
            targetWidth,
            duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var closeWidthAnimation = new DoubleAnimation(
            currentCloseWidth,
            targetCloseWidth,
            duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var closeOpacityAnimation = new DoubleAnimation(
            currentCloseOpacity,
            targetCloseOpacity,
            duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        widthAnimation.Completed += (_, _) =>
        {
            if (_disposed ||
                generation != _wpfNativeHoverAnimationGeneration ||
                !_wpfNativeHoverGeometryOwned)
            {
                return;
            }

            _wpfNativeHoverAnimationRunning = false;
            SetWpfNativeHoverVisualBase();
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"wpf.native-hover phase=clock-complete " +
                $"paper={_options.DiagnosticId} target={_wpfNativeHoverTargetSurface} " +
                $"widthDip={_wpfNativeHoverTargetWidthDip:F3} " +
                $"closeDip={_wpfNativeHoverTargetCloseWidthDip:F3} " +
                $"targetDeviceWidth={targetFrame.Bounds.Width}");
#endif
        };

        VisualSurface.BeginAnimation(
            FrameworkElement.WidthProperty,
            widthAnimation,
            HandoffBehavior.SnapshotAndReplace);
        closeColumn.BeginAnimation(
            ColumnDefinition.MaxWidthProperty,
            closeWidthAnimation,
            HandoffBehavior.SnapshotAndReplace);
        CloseArea.BeginAnimation(
            UIElement.OpacityProperty,
            closeOpacityAnimation,
            HandoffBehavior.SnapshotAndReplace);
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"wpf.native-hover phase=endpoint-armed paper={_options.DiagnosticId} " +
            $"target={targetFrame.Surface} fromWidthDip={currentWidth:F3} " +
            $"targetWidthDip={targetWidth:F3} fromCloseDip={currentCloseWidth:F3} " +
            $"targetCloseDip={targetCloseWidth:F3} targetDeviceWidth={targetFrame.Bounds.Width} " +
            $"durationMs={EdgeCapsuleLayout.HorizontalResizeMilliseconds}");
#endif
    }
}
