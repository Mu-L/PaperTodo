using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    private Border? _pluginContentLayer;

    public void SetPluginContent(FrameworkElement? content, string? toolTip)
    {
        if (_disposed)
        {
            return;
        }

        _pluginContentLayer ??= CreatePluginContentLayer();
        if (content == null)
        {
            _pluginContentLayer.Child = null;
            _pluginContentLayer.Visibility = Visibility.Collapsed;
            Icon.Visibility = Visibility.Visible;
            Label.Visibility = Visibility.Visible;
            ContentArea.ToolTip = null;
            return;
        }

        if (content is Window ||
            (content.Parent != null &&
             !ReferenceEquals(content.Parent, _pluginContentLayer)))
        {
            throw new InvalidOperationException(
                "Capsule content must be a fresh FrameworkElement or the current hosted view.");
        }

        content.IsHitTestVisible = false;
        content.Focusable = false;
        Icon.Visibility = Visibility.Collapsed;
        Label.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(_pluginContentLayer.Child, content))
        {
            _pluginContentLayer.Child = content;
        }
        // Keep the compact plugin tree layout-resident for the same reason as ContentGrid: a
        // closing preview can restore it by opacity without exposing a one-frame empty shell.
        _pluginContentLayer.Visibility = Visibility.Visible;
        _pluginContentLayer.Opacity = _previewVisible ? 0 : 1;
        ContentArea.ToolTip = toolTip;
    }

    private Border CreatePluginContentLayer()
    {
        var layer = new Border
        {
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(layer, 10);
        ContentHost.Children.Add(layer);
        return layer;
    }
}
