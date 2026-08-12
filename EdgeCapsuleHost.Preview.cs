using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    private Border? _previewViewportLayer;
    private Border? _previewContentLayer;
    private FrameworkElement? _previewContent;
    private int _previewContentStageGeneration;
    private bool _previewVisible;
    private bool _previewInteractiveCaptureLease;
    private bool _compactLabelSuppressedForPreview;
    private readonly TranslateTransform _compactContentAnchorTransform = new();
    private double _compactContentAnchorWidthDip = double.NaN;
    private double _compactContentAnchorCloseWidthDip = double.NaN;
    private long _previewInteractiveCaptureGraceUntil;

    private const int PreviewInteractiveCaptureGraceMilliseconds = 250;
    private const int CompactLabelFadeMilliseconds = 35;

    public bool IsPreviewPointerCaptureActive
    {
        get
        {
            if (_disposed || !_previewVisible || _previewContent == null)
            {
                return false;
            }

            var captured = Mouse.Captured as DependencyObject;
            if (captured != null && IsDescendantOfPreview(captured))
            {
                return true;
            }

            if (!_previewInteractiveCaptureLease)
            {
                return false;
            }

            // ComboBox, ContextMenu and similar controls move capture into a separate Popup HWND,
            // so it is no longer a visual descendant of the mini tree. A lease armed by the
            // initiating pointer gesture lets that same-dispatcher capture keep the preview open.
            if (captured != null && ReferenceEquals(captured.Dispatcher, Dispatcher))
            {
                return true;
            }
            if (Mouse.LeftButton == MouseButtonState.Pressed ||
                Mouse.RightButton == MouseButtonState.Pressed ||
                Environment.TickCount64 <= _previewInteractiveCaptureGraceUntil)
            {
                return true;
            }

            _previewInteractiveCaptureLease = false;
            return false;
        }
    }

    private void ArmPreviewInteractiveCaptureLease()
    {
        if (_disposed || !_previewVisible)
        {
            return;
        }

        _previewInteractiveCaptureLease = true;
        _previewInteractiveCaptureGraceUntil =
            Environment.TickCount64 + PreviewInteractiveCaptureGraceMilliseconds;
    }

    public bool OwnsPreviewContent(FrameworkElement content) =>
        !_disposed &&
        ReferenceEquals(_previewContent, content) &&
        ReferenceEquals(content.Parent, _previewContentLayer);

    public bool HasPreviewContent => !_disposed && _previewContent != null;

    // Stage and prepare the final-size preview tree before the visual transaction begins. The
    // viewport changes size during the shell animation, but the content tree itself keeps its final
    // layout size so shrinking never makes a ScrollViewer or wrapped text reflow frame-by-frame.
    public bool StagePreviewContent(
        FrameworkElement content,
        double contentWidthDip,
        double contentHeightDip)
    {
        if (_disposed)
        {
            return false;
        }
        if (content is Window ||
            (content.Parent != null &&
             !ReferenceEquals(content.Parent, _previewContentLayer)))
        {
            throw new InvalidOperationException(
                "Preview content must be a fresh, unparented FrameworkElement.");
        }

        EnsurePreviewLayers();
        if (_previewContentLayer == null)
        {
            return false;
        }

        var stageGeneration = unchecked(++_previewContentStageGeneration);

        if (!ReferenceEquals(_previewContent, content))
        {
            _previewInteractiveCaptureLease = false;
            _previewInteractiveCaptureGraceUntil = 0;
        }

        if (_previewContentLayer.Child != null &&
            !ReferenceEquals(_previewContentLayer.Child, content))
        {
            _previewContentLayer.Child = null;
            if (!IsPreviewContentStageCurrent(stageGeneration))
            {
                return false;
            }
        }

        contentWidthDip = Math.Max(1, contentWidthDip);
        contentHeightDip = Math.Max(1, contentHeightDip);
        _previewContentLayer.Width = contentWidthDip;
        if (!IsPreviewContentStageCurrent(stageGeneration))
        {
            return false;
        }
        _previewContentLayer.Height = contentHeightDip;
        if (!IsPreviewContentStageCurrent(stageGeneration))
        {
            return false;
        }
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (!IsPreviewContentStageCurrent(stageGeneration))
        {
            return false;
        }
        content.VerticalAlignment = VerticalAlignment.Stretch;
        if (!IsPreviewContentStageCurrent(stageGeneration))
        {
            return false;
        }

        if (content is EdgeCapsuleLivePreviewView livePreview)
        {
            livePreview.PrepareForFirstDisplay();
        }
        // Live rebuilds and dependency-property callbacks are application/plugin code and may pump
        // messages. If one closed/replaced this preview, do not let the older Stage resume and
        // overwrite the newer child.
        if (!IsPreviewContentStageCurrent(stageGeneration))
        {
            return false;
        }

        _previewContent = content;
        _previewContentLayer.Child = content;
        if (_disposed ||
            stageGeneration != _previewContentStageGeneration ||
            !OwnsPreviewContent(content))
        {
            return false;
        }
        if (_previewViewportLayer != null)
        {
            // Join layout before the transaction can make the layer opaque. The compact tree stays
            // painted at progress zero, so the first compositor frame still has valid content even
            // if this newly mounted tree needs one layout pass.
            _previewViewportLayer.Visibility = Visibility.Visible;
            if (_disposed ||
                stageGeneration != _previewContentStageGeneration ||
                !OwnsPreviewContent(content))
            {
                return false;
            }
            _previewViewportLayer.Opacity = 0;
            if (_disposed ||
                stageGeneration != _previewContentStageGeneration ||
                !OwnsPreviewContent(content))
            {
                return false;
            }
            _previewViewportLayer.IsHitTestVisible = false;
        }
        return IsPreviewContentStageCurrent(stageGeneration) &&
            OwnsPreviewContent(content);
    }

    private bool IsPreviewContentStageCurrent(int generation) =>
        !_disposed && generation == _previewContentStageGeneration;

    public bool ReplacePreviewContent(
        FrameworkElement expectedContent,
        FrameworkElement content,
        double contentWidthDip,
        double contentHeightDip)
    {
        if (_disposed || !OwnsPreviewContent(expectedContent))
        {
            return false;
        }

        // Preserve the layer state already produced through Apply(frame). Replacing a staged tree
        // is content lifecycle work, not a second presentation entry point, and therefore cannot
        // advance geometry from an uncommitted native batch.
        var wasVisible = _previewVisible;
        var progress = _previewViewportLayer?.Opacity ?? 0;
        var wasHitTestVisible = _previewViewportLayer?.IsHitTestVisible == true;
        if (!StagePreviewContent(content, contentWidthDip, contentHeightDip))
        {
            return false;
        }

        var replacementGeneration = _previewContentStageGeneration;
        _previewVisible = wasVisible;
        ApplyPreviewLayerState(wasVisible, progress, wasHitTestVisible);
        if (_disposed ||
            replacementGeneration != _previewContentStageGeneration ||
            !OwnsPreviewContent(content))
        {
            return false;
        }
        ApplyCompactContentProgress(wasVisible ? progress : 0);
        return !_disposed &&
            replacementGeneration == _previewContentStageGeneration &&
            OwnsPreviewContent(content);
    }

    public void ClearPreviewContent()
    {
        if (_disposed)
        {
            return;
        }

        if (DetachPreviewContent())
        {
            ApplyCompactContentProgress(0);
        }
    }

    private void EnsurePreviewLayers()
    {
        if (_previewViewportLayer != null && _previewContentLayer != null)
        {
            return;
        }

        var contentLayer = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = false,
            IsHitTestVisible = true
        };
        var viewportLayer = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = contentLayer
        };
        Panel.SetZIndex(viewportLayer, 30);
        ContentHost.Children.Add(viewportLayer);
        _previewContentLayer = contentLayer;
        _previewViewportLayer = viewportLayer;
    }

    private void ApplyPreviewViewportClip(
        EdgeCapsulePresentationFrame frame,
        double bodyHeight)
    {
        if (_previewViewportLayer == null)
        {
            return;
        }

        var dpiScaleX = Math.Max(1, frame.DpiScaleX);
        var bodyWindowWidthDip = frame.BodyWindowWidthDevice / dpiScaleX;
        var width = Math.Max(
            1,
            bodyWindowWidthDip - _options.WindowChromeMargin);
        var height = Math.Max(1, bodyHeight);
        var corners = ContentArea.CornerRadius;
        var maximumRadius = Math.Min(width, height) / 2;
        var topLeft = Math.Clamp(corners.TopLeft, 0, maximumRadius);
        var topRight = Math.Clamp(corners.TopRight, 0, maximumRadius);
        var bottomRight = Math.Clamp(corners.BottomRight, 0, maximumRadius);
        var bottomLeft = Math.Clamp(corners.BottomLeft, 0, maximumRadius);

        var clip = new StreamGeometry();
        using (var geometry = clip.Open())
        {
            geometry.BeginFigure(
                new Point(topLeft, 0),
                isFilled: true,
                isClosed: true);
            geometry.LineTo(
                new Point(width - topRight, 0),
                isStroked: true,
                isSmoothJoin: false);
            if (topRight > 0)
            {
                geometry.ArcTo(
                    new Point(width, topRight),
                    new Size(topRight, topRight),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
            else
            {
                geometry.LineTo(
                    new Point(width, 0),
                    isStroked: true,
                    isSmoothJoin: false);
            }

            geometry.LineTo(
                new Point(width, height - bottomRight),
                isStroked: true,
                isSmoothJoin: false);
            if (bottomRight > 0)
            {
                geometry.ArcTo(
                    new Point(width - bottomRight, height),
                    new Size(bottomRight, bottomRight),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
            else
            {
                geometry.LineTo(
                    new Point(width, height),
                    isStroked: true,
                    isSmoothJoin: false);
            }

            geometry.LineTo(
                new Point(bottomLeft, height),
                isStroked: true,
                isSmoothJoin: false);
            if (bottomLeft > 0)
            {
                geometry.ArcTo(
                    new Point(0, height - bottomLeft),
                    new Size(bottomLeft, bottomLeft),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
            else
            {
                geometry.LineTo(
                    new Point(0, height),
                    isStroked: true,
                    isSmoothJoin: false);
            }

            geometry.LineTo(
                new Point(0, topLeft),
                isStroked: true,
                isSmoothJoin: false);
            if (topLeft > 0)
            {
                geometry.ArcTo(
                    new Point(topLeft, 0),
                    new Size(topLeft, topLeft),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
        }
        clip.Freeze();
        _previewViewportLayer.Clip = clip;
    }

    private bool ApplyPreviewPresentation(
        EdgeCapsulePresentationFrame frame)
    {
        var presentationContentGeneration = _previewContentStageGeneration;
        var dpiScaleY = Math.Max(1, frame.DpiScaleY);
        var chromeMarginDevice = Math.Max(
            0,
            (int)Math.Round(
                _options.WindowChromeMargin * dpiScaleY,
                MidpointRounding.AwayFromZero));
        var bodyHeightDevice = Math.Max(
            1,
            frame.Bounds.Height - chromeMarginDevice * 2);
        var bodyHeight = bodyHeightDevice / dpiScaleY;

        // VisualSurface owns the exact device-pixel frame in both axes. Keep all three shells
        // stretched inside that one surface so width and height are committed by the same WPF
        // layout pass; assigning three independent heights here makes the native height resize
        // visibly lead the surface-width update while a preview is shrinking.
        Chrome.VerticalAlignment = VerticalAlignment.Stretch;
        Chrome.Height = double.NaN;
        Shell.VerticalAlignment = VerticalAlignment.Stretch;
        Shell.Height = double.NaN;
        Outline.VerticalAlignment = VerticalAlignment.Stretch;
        Outline.Height = double.NaN;

        var compactBoundsHeightDevice = Math.Max(
            1,
            (int)Math.Round(
                (_options.BodyHeight + _options.WindowChromeMargin * 2) * dpiScaleY,
                MidpointRounding.AwayFromZero));
        var compactBodyHeight = Math.Max(
            1,
            compactBoundsHeightDevice - chromeMarginDevice * 2) / dpiScaleY;
        var previewBodyHeight = compactBodyHeight + 1;
        if (_previewContentLayer is { Height: var contentHeight } &&
            double.IsFinite(contentHeight) &&
            contentHeight > 0)
        {
            var previewBoundsHeightDevice = Math.Max(
                1,
                (int)Math.Round(
                    (contentHeight + _options.WindowChromeMargin * 2) * dpiScaleY,
                    MidpointRounding.AwayFromZero));
            previewBodyHeight = Math.Max(
                1,
                previewBoundsHeightDevice - chromeMarginDevice * 2) / dpiScaleY;
        }

        var heightExpanded =
            bodyHeight > compactBodyHeight + 0.5;
        var previewSurface =
            frame.Surface == EdgeCapsuleSurfaceKind.DockedPreview;
        // Opening starts with the old compact bounds, while an outgoing preview deliberately keeps
        // DockedPreview until the width/height transition reaches its common final frame. Surface,
        // not the intermediate height threshold, therefore owns the preview tree's lifetime.
        var retainPreview = previewSurface || heightExpanded;
        var previewProgress = Math.Clamp(
            (bodyHeight - compactBodyHeight) /
                Math.Max(1, previewBodyHeight - compactBodyHeight),
            0,
            1);

        var previousPreviewVisible = _previewVisible;
        var previousPreviewProgress = _previewViewportLayer?.Opacity ?? 0;
        var currentCloseWidth = _appliedCloseWidth;
        var previousCloseWidth = _appliedFrame.Visible
            ? EdgeCapsuleGeometry.CloseWidthForAppliedDeviceWidth(
                _appliedFrame.Bounds.Width,
                _appliedFrame.BodyWindowWidthDevice,
                _appliedFrame.DpiScaleX,
                _appliedFrame.MaximumCloseWidthDip)
            : currentCloseWidth;
        var hasContent =
            _previewViewportLayer != null &&
            _previewContentLayer != null &&
            _previewContent != null;
        _previewVisible = retainPreview && hasContent;

        var openingPreview = _previewVisible &&
            (!previousPreviewVisible ||
             previewProgress > previousPreviewProgress + 0.0001);
        var closingPreview = _previewVisible &&
            previousPreviewVisible &&
            previewProgress + 0.0001 < previousPreviewProgress;

        if (openingPreview)
        {
            CaptureCompactContentAnchor(frame);
            SetCompactLabelSuppressedForPreview(true);
        }

        if (_previewVisible)
        {
            if (closingPreview &&
                _compactLabelSuppressedForPreview &&
                TryRetargetCompactContentAnchorForPreviewClose(
                    previousPreviewProgress,
                    previewProgress,
                    previousCloseWidth,
                    currentCloseWidth))
            {
                ScheduleCompactLabelRestoreForPreviewClose(
                    previousPreviewProgress,
                    previewProgress);
            }
            ApplyCompactContentAnchor(frame);
        }
        else if (!retainPreview)
        {
            RestoreCompactContentAnchor();
            // If the last-35-ms fade is already running, its logical state is unsuppressed and this
            // is a no-op; the final compact frame therefore cannot restart or truncate that fade.
            SetCompactLabelSuppressedForPreview(false);
        }

        if (_previewContentLayer != null)
        {
            _previewContentLayer.HorizontalAlignment = frame.Edge == EdgeCapsuleEdge.Left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }
        ApplyPreviewViewportClip(frame, bodyHeight);
        ApplyPreviewLayerState(
            _previewVisible,
            _previewVisible ? previewProgress : 0,
            _previewVisible &&
                previewProgress > 0.001 &&
                frame.IsHitTestVisible);
        if (!IsPreviewContentStageCurrent(presentationContentGeneration))
        {
            return false;
        }

        // Compact content stays layout-resident so closing never pays a fresh layout cost. The
        // label has its own fixed-position 35 ms opacity channel; only the icon/custom compact
        // content continues to use the preview progress cross-fade.
        ApplyCompactContentProgress(
            _previewVisible ? previewProgress : 0);
        if (!IsPreviewContentStageCurrent(presentationContentGeneration))
        {
            return false;
        }

        if (!retainPreview && hasContent)
        {
            // Keep the outgoing final-size tree while the viewport shrinks, then release it on the
            // common final compact frame. Each host owns this lifetime independently, so a rapid
            // third-card transfer cannot expose an older still-shrinking shell without content.
            if (!DetachPreviewContent())
            {
                return false;
            }
        }
        return true;
    }

    private void CaptureCompactContentAnchor(EdgeCapsulePresentationFrame frame)
    {
        if (!double.IsFinite(_compactContentAnchorCloseWidthDip))
        {
            _compactContentAnchorCloseWidthDip = _appliedCloseWidth;
        }
        if (double.IsFinite(_compactContentAnchorWidthDip) &&
            _compactContentAnchorWidthDip > 0)
        {
            return;
        }

        var actualWidth = ContentGrid.ActualWidth;
        if (double.IsFinite(actualWidth) && actualWidth > 0.5)
        {
            _compactContentAnchorWidthDip = actualWidth;
            return;
        }

        var source = _appliedFrame.Visible
            ? _appliedFrame
            : frame;
        var sourceScaleX = Math.Max(1, source.DpiScaleX);
        _compactContentAnchorWidthDip = Math.Max(
            1,
            source.BodyWindowWidthDevice / sourceScaleX -
                ContentGrid.Margin.Left - ContentGrid.Margin.Right);
    }

    private bool TryRetargetCompactContentAnchorForPreviewClose(
        double previousPreviewProgress,
        double previewProgress,
        double previousCloseWidth,
        double currentCloseWidth)
    {
        if (previousPreviewProgress <= 0.0001)
        {
            return false;
        }

        // Every interpolated field uses the same eased transition progress. On a close, preview
        // height remaining and close-width distance to the target therefore share the same ratio.
        // Solve the target close width from the first two closing samples, then move the invisible
        // compact title to that final screen position before its last-35-ms fade begins.
        var remainingRatio = Math.Clamp(
            previewProgress / previousPreviewProgress,
            0,
            1);
        var denominator = 1 - remainingRatio;
        if (denominator <= 0.0001)
        {
            return false;
        }

        var targetCloseWidth =
            (currentCloseWidth - previousCloseWidth * remainingRatio) /
            denominator;
        if (!double.IsFinite(targetCloseWidth))
        {
            return false;
        }

        _compactContentAnchorCloseWidthDip = Math.Clamp(
            targetCloseWidth,
            0,
            Math.Max(0, _maximumCloseWidth));
        return true;
    }

    private void ApplyCompactContentAnchor(EdgeCapsulePresentationFrame frame)
    {
        if (!double.IsFinite(_compactContentAnchorWidthDip) ||
            _compactContentAnchorWidthDip <= 0 ||
            !double.IsFinite(_compactContentAnchorCloseWidthDip))
        {
            CaptureCompactContentAnchor(frame);
        }

        ContentGrid.Width = Math.Max(1, _compactContentAnchorWidthDip);
        ContentGrid.Height = _options.BodyHeight;
        ContentGrid.HorizontalAlignment = frame.Edge == EdgeCapsuleEdge.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        ContentGrid.VerticalAlignment = VerticalAlignment.Top;

        var closeWidthDelta = _appliedCloseWidth - _compactContentAnchorCloseWidthDip;
        _compactContentAnchorTransform.X = frame.Edge == EdgeCapsuleEdge.Left
            ? -closeWidthDelta
            : closeWidthDelta;
        _compactContentAnchorTransform.Y = 0;
        if (!ReferenceEquals(ContentGrid.RenderTransform, _compactContentAnchorTransform))
        {
            ContentGrid.RenderTransform = _compactContentAnchorTransform;
        }
    }

    private void RestoreCompactContentAnchor()
    {
        _compactContentAnchorWidthDip = double.NaN;
        _compactContentAnchorCloseWidthDip = double.NaN;
        _compactContentAnchorTransform.X = 0;
        _compactContentAnchorTransform.Y = 0;
        if (ReferenceEquals(ContentGrid.RenderTransform, _compactContentAnchorTransform))
        {
            ContentGrid.RenderTransform = null;
        }
        ContentGrid.Width = double.NaN;
        ContentGrid.Height = double.NaN;
        ContentGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        ContentGrid.VerticalAlignment = VerticalAlignment.Center;
    }

    private void ApplyPreviewLayerState(
        bool visible,
        double progress,
        bool hitTestVisible)
    {
        if (_previewViewportLayer == null)
        {
            return;
        }

        _previewViewportLayer.Visibility =
            visible ? Visibility.Visible : Visibility.Collapsed;
        _previewViewportLayer.Opacity = visible ? progress : 0;
        _previewViewportLayer.IsHitTestVisible = visible && hitTestVisible;
    }

    private void ApplyCompactContentProgress(double previewProgress)
    {
        var progress = Math.Clamp(previewProgress, 0, 1);
        var compactOpacity = 1 - progress;

        // Keep compact content in layout. The label must not inherit preview-progress opacity or it
        // would visually travel with the expanding/shrinking card; its independent animation below
        // is the only opacity channel allowed to affect it.
        ContentGrid.Visibility = Visibility.Visible;
        ContentGrid.Opacity = 1;
        Icon.Opacity = compactOpacity;
        ContentGrid.IsHitTestVisible = progress <= 0.001;
        if (_pluginContentLayer != null)
        {
            _pluginContentLayer.Visibility = _pluginContentLayer.Child != null
                ? Visibility.Visible
                : Visibility.Collapsed;
            _pluginContentLayer.Opacity = compactOpacity;
        }

        ContentArea.Background = progress > 0.001
            ? Brushes.Transparent
            : ContentArea.IsMouseOver
                ? _hoverBrush
                : Brushes.Transparent;
    }

    private void SetCompactLabelSuppressedForPreview(bool suppressed)
    {
        if (_compactLabelSuppressedForPreview == suppressed)
        {
            return;
        }

        _compactLabelSuppressedForPreview = suppressed;
        var targetOpacity = suppressed ? 0d : 1d;
        var currentOpacity = Math.Clamp(Label.Opacity, 0, 1);

        // The compact title never scales with the preview card. It remains on the compact anchor
        // and only changes opacity over this short independent animation.
        Label.BeginAnimation(UIElement.OpacityProperty, null);
        Label.Opacity = targetOpacity;
        if (Math.Abs(currentOpacity - targetOpacity) <= 0.001)
        {
            return;
        }

        Label.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation
            {
                From = currentOpacity,
                To = targetOpacity,
                Duration = new Duration(
                    TimeSpan.FromMilliseconds(CompactLabelFadeMilliseconds)),
                FillBehavior = FillBehavior.Stop
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ScheduleCompactLabelRestoreForPreviewClose(
        double previousPreviewProgress,
        double previewProgress)
    {
        if (!_compactLabelSuppressedForPreview ||
            previousPreviewProgress <= 0.0001)
        {
            return;
        }

        // Preview geometry uses EaseOutCubic. During a close, normalized height remaining is
        // remainingTime^3, so cube-rooting the first observed progress ratio recovers the remaining
        // fraction of the 200 ms preview transition without introducing a second timing source.
        var remainingFraction = Math.Cbrt(Math.Clamp(
            previewProgress / previousPreviewProgress,
            0,
            1));
        var remainingMilliseconds = Math.Max(
            1,
            EdgeCapsuleLayout.SlotMoveMilliseconds * remainingFraction);
        var fadeStartMilliseconds = Math.Max(
            0,
            remainingMilliseconds - CompactLabelFadeMilliseconds);

        _compactLabelSuppressedForPreview = false;
        Label.BeginAnimation(UIElement.OpacityProperty, null);
        Label.Opacity = 1;

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(
                TimeSpan.FromMilliseconds(remainingMilliseconds)),
            FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(
                TimeSpan.FromMilliseconds(fadeStartMilliseconds))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(
                TimeSpan.FromMilliseconds(remainingMilliseconds))));
        Label.BeginAnimation(
            UIElement.OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private bool DetachPreviewContent()
    {
        int detachGeneration;
        unchecked
        {
            detachGeneration = ++_previewContentStageGeneration;
        }
        if (_previewContentLayer != null)
        {
            _previewContentLayer.Child = null;
            if (!IsPreviewContentStageCurrent(detachGeneration))
            {
                return false;
            }
            _previewContentLayer.Width = double.NaN;
            if (!IsPreviewContentStageCurrent(detachGeneration))
            {
                return false;
            }
            _previewContentLayer.Height = double.NaN;
            if (!IsPreviewContentStageCurrent(detachGeneration))
            {
                return false;
            }
        }
        _previewContent = null;
        _previewVisible = false;
        _previewInteractiveCaptureLease = false;
        _previewInteractiveCaptureGraceUntil = 0;
        RestoreCompactContentAnchor();
        if (_previewViewportLayer != null)
        {
            _previewViewportLayer.Visibility = Visibility.Collapsed;
            if (!IsPreviewContentStageCurrent(detachGeneration))
            {
                return false;
            }
            _previewViewportLayer.Opacity = 0;
            if (!IsPreviewContentStageCurrent(detachGeneration))
            {
                return false;
            }
            _previewViewportLayer.IsHitTestVisible = false;
        }
        return IsPreviewContentStageCurrent(detachGeneration);
    }

    private bool IsPreviewInteractiveSource(
        DependencyObject? source)
    {
        if (!_previewVisible ||
            _previewViewportLayer == null ||
            _previewContentLayer == null ||
            _previewContent == null)
        {
            return false;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, _previewViewportLayer) ||
                ReferenceEquals(current, _previewContentLayer))
            {
                return false;
            }
            // The edge host deliberately uses WS_EX_NOACTIVATE. Text editors therefore cannot
            // acquire keyboard focus here; treating them as background opens the full paper
            // instead of presenting a control that appears editable but cannot accept typing.
            if (current is TextBoxBase or PasswordBox)
            {
                return false;
            }
            if (EdgeCapsulePreviewInteraction.GetConsumesPointer(current) ||
                PaperMiniViewInteraction.GetConsumesPointer(current) ||
                current is ButtonBase or
                    Selector or
                    ScrollBar or
                    Thumb or
                    MenuItem or
                    Hyperlink)
            {
                return true;
            }

            current = PreviewVisualParent(current);
        }

        return false;
    }

    private bool IsDescendantOfPreview(DependencyObject current)
    {
        DependencyObject? candidate = current;
        while (candidate != null)
        {
            if (ReferenceEquals(candidate, _previewViewportLayer) ||
                ReferenceEquals(candidate, _previewContentLayer) ||
                ReferenceEquals(candidate, _previewContent))
            {
                return true;
            }
            candidate = PreviewVisualParent(candidate);
        }
        return false;
    }

    private static DependencyObject? PreviewVisualParent(
        DependencyObject current)
    {
        if (current is Visual ||
            current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }
        if (current is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }
        if (current is ContentElement content)
        {
            return ContentOperations.GetParent(content);
        }
        return null;
    }
}
