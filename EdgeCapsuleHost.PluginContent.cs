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
        _pluginContentLayer.Visibility = Visibility.Visible;
        ContentArea.ToolTip = toolTip;
    }

    private Border CreatePluginContentLayer()
    {
        var layer = new Border
        {
            Background = null,
            Padding = new Thickness(0, 0, _options.LeftPadding, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(layer, 0);
        Grid.SetColumnSpan(layer, 2);
        Panel.SetZIndex(layer, 10);
        ContentGrid.Children.Add(layer);
        return layer;
    }
}
