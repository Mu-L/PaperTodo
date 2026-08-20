using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace PaperTodo;

/// <summary>
/// Branch-only adapter for WPF-native timing experiments. Resting <-> Hovered still uses the
/// first probe below; Preview -> Resting adds a second probe where only the outer VisualSurface
/// width/height are owned by a stock WPF animation clock while the legacy Presenter continues to
/// drive preview content opacity/lifetime as the control group.
/// </summary>
internal sealed partial class EdgeCapsuleHost
{
    private const double WpfNativeHoverSettleSuppressionWidthDip = 1_000_000;

    private bool _wpfNativePreviewCloseShellOwned;
    private bool _wpfNativePreviewCloseShellAnimationRunning;
    private int _wpfNativePreviewCloseShellGeneration;
    private long _wpfNativePreviewCloseShellStartedAt;
    private EdgeCapsulePresentationFrame _wpfNativePreviewCloseShellTarget =
        EdgeCapsulePresentationFrame.Hidden;

    public bool Apply(
        EdgeCapsulePresentationFrame frame,
        Func<EdgeCapsulePresentationFrame> targetFrameProvider)
    {
        ArgumentNullException.ThrowIfNull(targetFrameProvider);

        PrepareWpfNativePreviewCloseShell(frame, targetFrameProvider);

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

        if (!applied)
        {
            CancelWpfNativePreviewCloseShell("apply-failed");
            return false;
        }

        if (!_wpfNativeHoverGeometryOwned ||
            generationBeforeApply == _wpfNativeHoverAnimationGeneration)
        {
            return true;
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
            return true;
        }

        ArmExactWpfNativeHoverEndpoint(frame, targetFrame);
        return true;
    }

    private void PrepareWpfNativePreviewCloseShell(
        EdgeCapsulePresentationFrame frame,
        Func<EdgeCapsulePresentationFrame> targetFrameProvider)
    {
        if (_disposed)
        {
            return;
        }

        if (_wpfNativePreviewCloseShellOwned)
        {
            if (!CanContinueWpfNativePreviewCloseShell(frame))
            {
                CancelWpfNativePreviewCloseShell("geometry-diverged");
            }
            return;
        }

        var previous = _appliedFrame;
        if (!previous.Visible ||
            previous.Surface != EdgeCapsuleSurfaceKind.DockedPreview ||
            !frame.Visible)
        {
            return;
        }

        // Start only after the first real shrinking sample. That keeps a stationary Preview out of
        // the experiment and gives us the already-arranged preview shell as the exact visual start.
        var shrinking =
            frame.Bounds.Width < previous.Bounds.Width ||
            frame.Bounds.Height < previous.Bounds.Height;
        if (!shrinking)
        {
            return;
        }

        var targetFrame = targetFrameProvider();
        if (!CanStartWpfNativePreviewCloseShell(previous, targetFrame))
        {
            return;
        }

        StartWpfNativePreviewCloseShell(previous, targetFrame);
    }

    private bool CanStartWpfNativePreviewCloseShell(
        EdgeCapsulePresentationFrame startFrame,
        EdgeCapsulePresentationFrame targetFrame) =>
        startFrame.Visible &&
        targetFrame.Visible &&
        startFrame.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
        targetFrame.Surface == EdgeCapsuleSurfaceKind.DockedResting &&
        targetFrame.IsUsable &&
        startFrame.HostBounds == targetFrame.HostBounds &&
        startFrame.Edge == targetFrame.Edge &&
        startFrame.WallDeviceX == targetFrame.WallDeviceX &&
        startFrame.Bounds.Top == targetFrame.Bounds.Top &&
        Math.Abs(startFrame.DpiScaleX - targetFrame.DpiScaleX) < 0.001 &&
        Math.Abs(startFrame.DpiScaleY - targetFrame.DpiScaleY) < 0.001 &&
        targetFrame.Bounds.Width <= startFrame.Bounds.Width &&
        targetFrame.Bounds.Height <= startFrame.Bounds.Height &&
        (targetFrame.Bounds.Width < startFrame.Bounds.Width ||
         targetFrame.Bounds.Height < startFrame.Bounds.Height);

    private bool CanContinueWpfNativePreviewCloseShell(
        EdgeCapsulePresentationFrame frame)
    {
        var target = _wpfNativePreviewCloseShellTarget;
        if (!frame.Visible ||
            frame.Surface is not (
                EdgeCapsuleSurfaceKind.DockedPreview or
                EdgeCapsuleSurfaceKind.DockedResting) ||
            frame.HostBounds != target.HostBounds ||
            frame.Edge != target.Edge ||
            frame.WallDeviceX != target.WallDeviceX ||
            frame.Bounds.Top != target.Bounds.Top ||
            Math.Abs(frame.DpiScaleX - target.DpiScaleX) > 0.001 ||
            Math.Abs(frame.DpiScaleY - target.DpiScaleY) > 0.001)
        {
            return false;
        }

        // A re-open retarget reverses size direction while Surface can still be DockedPreview.
        // Cancel before the normal Apply so the old sampled path becomes authoritative again.
        var previous = _appliedFrame;
        return !previous.Visible ||
            frame.Bounds.Width <= previous.Bounds.Width &&
            frame.Bounds.Height <= previous.Bounds.Height;
    }

    private void StartWpfNativePreviewCloseShell(
        EdgeCapsulePresentationFrame startFrame,
        EdgeCapsulePresentationFrame targetFrame)
    {
        var startScaleX = Math.Max(1, startFrame.DpiScaleX);
        var startScaleY = Math.Max(1, startFrame.DpiScaleY);
        var targetScaleX = Math.Max(1, targetFrame.DpiScaleX);
        var targetScaleY = Math.Max(1, targetFrame.DpiScaleY);
        var fromWidth =
            double.IsFinite(VisualSurface.ActualWidth) &&
            VisualSurface.ActualWidth > 0
                ? VisualSurface.ActualWidth
                : startFrame.Bounds.Width / startScaleX;
        var fromHeight =
            double.IsFinite(VisualSurface.ActualHeight) &&
            VisualSurface.ActualHeight > 0
                ? VisualSurface.ActualHeight
                : startFrame.Bounds.Height / startScaleY;
        var targetWidth = targetFrame.Bounds.Width / targetScaleX;
        var targetHeight = targetFrame.Bounds.Height / targetScaleY;

        ++_wpfNativePreviewCloseShellGeneration;
        ClearWpfNativePreviewCloseShellAnimations();
        _wpfNativePreviewCloseShellOwned = true;
        _wpfNativePreviewCloseShellAnimationRunning = true;
        _wpfNativePreviewCloseShellTarget = targetFrame;
        _wpfNativePreviewCloseShellStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        var generation = _wpfNativePreviewCloseShellGeneration;

        var duration = new Duration(TimeSpan.FromMilliseconds(
            EdgeCapsuleLayout.SlotMoveMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var widthAnimation = new DoubleAnimation(
            fromWidth,
            targetWidth,
            duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var heightAnimation = new DoubleAnimation(
            fromHeight,
            targetHeight,
            duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        widthAnimation.Completed += (_, _) =>
        {
            if (_disposed ||
                generation != _wpfNativePreviewCloseShellGeneration ||
                !_wpfNativePreviewCloseShellOwned)
            {
                return;
            }

            var elapsedMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    _wpfNativePreviewCloseShellStartedAt);
            _wpfNativePreviewCloseShellAnimationRunning = false;
            ClearWpfNativePreviewCloseShellAnimations();
            VisualSurface.Width = targetWidth;
            VisualSurface.Height = targetHeight;
            VisualSurfaceOffset.X = 0;
            VisualSurfaceOffset.Y = 0;
            _wpfNativePreviewCloseShellOwned = false;
            _wpfNativePreviewCloseShellTarget =
                EdgeCapsulePresentationFrame.Hidden;
            _wpfNativePreviewCloseShellStartedAt = 0;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"wpf.native-preview-shell phase=clock-complete " +
                $"paper={_options.DiagnosticId} elapsedMs={elapsedMilliseconds:F3} " +
                $"targetWidthDip={targetWidth:F3} targetHeightDip={targetHeight:F3} " +
                $"targetDevice={targetFrame.Bounds.Width}x{targetFrame.Bounds.Height}");
#endif
        };

        VisualSurface.BeginAnimation(
            FrameworkElement.WidthProperty,
            widthAnimation,
            HandoffBehavior.SnapshotAndReplace);
        VisualSurface.BeginAnimation(
            FrameworkElement.HeightProperty,
            heightAnimation,
            HandoffBehavior.SnapshotAndReplace);
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"wpf.native-preview-shell phase=start paper={_options.DiagnosticId} " +
            $"fromWidthDip={fromWidth:F3} targetWidthDip={targetWidth:F3} " +
            $"fromHeightDip={fromHeight:F3} targetHeightDip={targetHeight:F3} " +
            $"fromDevice={startFrame.Bounds.Width}x{startFrame.Bounds.Height} " +
            $"targetDevice={targetFrame.Bounds.Width}x{targetFrame.Bounds.Height} " +
            $"durationMs={EdgeCapsuleLayout.SlotMoveMilliseconds}");
#endif
    }

    private void CancelWpfNativePreviewCloseShell(string reason)
    {
        if (!_wpfNativePreviewCloseShellOwned)
        {
            return;
        }

        var elapsedMilliseconds = _wpfNativePreviewCloseShellStartedAt == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _wpfNativePreviewCloseShellStartedAt);
        ++_wpfNativePreviewCloseShellGeneration;
        ClearWpfNativePreviewCloseShellAnimations();
        _wpfNativePreviewCloseShellOwned = false;
        _wpfNativePreviewCloseShellAnimationRunning = false;
        _wpfNativePreviewCloseShellTarget =
            EdgeCapsulePresentationFrame.Hidden;
        _wpfNativePreviewCloseShellStartedAt = 0;
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"wpf.native-preview-shell phase=cancel paper={_options.DiagnosticId} " +
            $"reason={reason} elapsedMs={elapsedMilliseconds:F3}");
#endif
    }

    private void ClearWpfNativePreviewCloseShellAnimations()
    {
        VisualSurface.BeginAnimation(FrameworkElement.WidthProperty, null);
        VisualSurface.BeginAnimation(FrameworkElement.HeightProperty, null);
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
        var currentWidth = _wpfNativeHoverGeometryOwned &&
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
