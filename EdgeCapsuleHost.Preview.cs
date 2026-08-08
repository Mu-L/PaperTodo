using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    private Border? _previewContentLayer;
    private FrameworkElement? _previewContent;
    private bool _previewVisible;

    public bool IsPreviewInteractionActive
    {
        get
        {
            if (_disposed || !_previewVisible || _previewContent == null)
            {
                return false;
            }

            if (_previewContent.IsKeyboardFocusWithin)
            {
                return true;
            }

            return Mouse.Captured is DependencyObject captured &&
                IsDescendantOfPreview(captured);
        }
    }

    // Stages the view tree only. Geometry, opacity, visibility and pointer eligibility remain
    // exclusively derived from the next Apply(frame) call.
    public void StagePreviewContent(FrameworkElement content)
    {
        if (_disposed)
        {
            return;
        }
        if (content is Window ||
            (content.Parent != null &&
             !ReferenceEquals(content.Parent, _previewContentLayer)))
        {
            throw new InvalidOperationException(
                "Preview content must be a fresh, unparented FrameworkElement.");
        }

        _previewContentLayer ??= CreatePreviewContentLayer();
        if (_previewContentLayer.Child != null &&
            !ReferenceEquals(_previewContentLayer.Child, content))
        {
            _previewContentLayer.Child = null;
        }

        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.VerticalAlignment = VerticalAlignment.Stretch;
        _previewContent = content;
        _previewContentLayer.Child = content;
    }

    public void ClearPreviewContent()
    {
        if (_disposed)
        {
            return;
        }

        DetachPreviewContent();
    }

    private Border CreatePreviewContentLayer()
    {
        // Transparent is intentional here: blank preview-body pixels must route the normal
        // left-click action. The layer is confined to ContentArea's shell column, so it cannot
        // cover CloseArea; native pixels outside the applied interactive bounds are still handled
        // by WM_NCHITTEST before WPF hit testing.
        var layer = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(layer, 30);
        ContentHost.Children.Add(layer);
        return layer;
    }

    private void ApplyPreviewPresentation(
        EdgeCapsulePresentationFrame frame)
    {
        var windowHeightDip =
            frame.Bounds.Height / Math.Max(1, frame.DpiScaleY);
        var bodyHeight = Math.Max(
            1,
            windowHeightDip - _options.WindowChromeMargin * 2);
        var outlineMargin =
            _options.WindowChromeMargin -
            _options.OutlineThickness +
            _options.OutlineOverlap;

        Chrome.Height = bodyHeight;
        Shell.Height = bodyHeight;
        Outline.Height = Math.Max(
            0,
            windowHeightDip - outlineMargin * 2);

        var heightExpanded =
            bodyHeight > _options.BodyHeight + 0.5;
        var previewSurface =
            frame.Surface == EdgeCapsuleSurfaceKind.DockedPreview;
        var visible = previewSurface || heightExpanded;
        var progress = Math.Clamp(
            (bodyHeight - _options.BodyHeight) / 48.0,
            0,
            1);

        var hasContent = _previewContentLayer != null && _previewContent != null;
        _previewVisible = visible && hasContent;
        ApplyPreviewLayerState(
            _previewVisible,
            _previewVisible ? progress : 0,
            _previewVisible && frame.IsHitTestVisible);
        ApplyCompactContentVisibility(suppressed: visible);

        if (!visible && hasContent)
        {
            // Keep the outgoing tree during the shrink, then release it on the first fully compact
            // frame. The controller may call ClearPreviewContent earlier for a rapid third card.
            DetachPreviewContent();
        }
    }

    private void ApplyPreviewLayerState(
        bool visible,
        double progress,
        bool hitTestVisible)
    {
        if (_previewContentLayer == null)
        {
            return;
        }

        _previewContentLayer.Visibility =
            visible ? Visibility.Visible : Visibility.Collapsed;
        _previewContentLayer.Opacity = visible ? progress : 0;
        _previewContentLayer.IsHitTestVisible = visible && hitTestVisible;
    }

    private void ApplyCompactContentVisibility(bool suppressed)
    {
        ContentGrid.Visibility =
            suppressed ? Visibility.Collapsed : Visibility.Visible;
        if (_pluginContentLayer != null)
        {
            _pluginContentLayer.Visibility = suppressed
                ? Visibility.Collapsed
                : _pluginContentLayer.Child != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (suppressed)
        {
            ContentArea.Background = Brushes.Transparent;
        }
    }

    private void DetachPreviewContent()
    {
        if (_previewContentLayer != null)
        {
            _previewContentLayer.Child = null;
        }
        _previewContent = null;
        _previewVisible = false;
    }

    private bool IsPreviewInteractiveSource(
        DependencyObject? source)
    {
        if (!_previewVisible ||
            _previewContentLayer == null ||
            _previewContent == null)
        {
            return false;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, _previewContentLayer))
            {
                return false;
            }
            if (EdgeCapsulePreviewInteraction.GetConsumesPointer(current) ||
                current is ButtonBase or
                    TextBoxBase or
                    Selector or
                    ScrollBar or
                    Thumb or
                    PasswordBox or
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
            if (ReferenceEquals(candidate, _previewContentLayer) ||
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
