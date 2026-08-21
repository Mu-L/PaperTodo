using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool _pluginTopBarLoadedHandlerRegistered;
    private StackPanel? _pluginTopBarButtonsHost;

    internal static void EnsurePluginTopBarLoadedHandler()
    {
        if (_pluginTopBarLoadedHandlerRegistered)
        {
            return;
        }
        _pluginTopBarLoadedHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is PaperWindow window && !window.IsClosed)
                {
                    window.RefreshPluginTopBarActions();
                }
            }));
    }

    internal void RefreshPluginTopBarActions()
    {
        if (IsClosed || _topBarActionButtonsHost == null)
        {
            return;
        }

        var state = _controller.GetPluginTopBarRenderState(_paper.Id);
        EnsurePluginTopBarButtonsHost();
        if (_pluginTopBarButtonsHost == null)
        {
            return;
        }

        _pluginTopBarButtonsHost.Children.Clear();
        foreach (var binding in state.Actions)
        {
            if (!binding.Action.Visible)
            {
                continue;
            }

            var button = IconButton("", binding.Action.ToolTip);
            button.IsEnabled = binding.Action.Enabled;
            button.Width = 23;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Content = CreatePluginTopBarIcon(button, binding.Action.Icon);
            button.Click += (_, _) =>
                _controller.InvokePluginTopBarAction(
                    binding,
                    _paper.Id,
                    _paper.Type,
                    NormalizeBodyProviderId(_paper.BodyProviderId));
            _pluginTopBarButtonsHost.Children.Add(button);
        }

        ApplyPluginHiddenHostTopBarActions(state.HiddenHostActions);
        UpdateTopBarResponsiveLayout();
    }

    private void EnsurePluginTopBarButtonsHost()
    {
        if (_topBarActionButtonsHost == null)
        {
            return;
        }

        _pluginTopBarButtonsHost ??= new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_pluginTopBarButtonsHost.Parent == null)
        {
            _topBarActionButtonsHost.Children.Insert(0, _pluginTopBarButtonsHost);
        }
    }

    private static UIElement CreatePluginTopBarIcon(
        Button button,
        PaperTopBarIcon icon)
    {
        if (icon.Kind == PaperTopBarIconKind.SvgPath)
        {
            var path = new Path
            {
                Data = Geometry.Parse(icon.Value),
                Width = AppTypography.Scale(13),
                Height = AppTypography.Scale(13),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            path.SetBinding(
                Shape.FillProperty,
                new Binding(nameof(Control.Foreground)) { Source = button });
            return path;
        }

        var text = new TextBlock
        {
            Text = icon.Value,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        text.SetBinding(
            TextBlock.ForegroundProperty,
            new Binding(nameof(Control.Foreground)) { Source = button });
        return text;
    }

    private void ApplyPluginHiddenHostTopBarActions(
        PaperHostTopBarActions hidden)
    {
        // First restore PaperTodo's normal user/settings-driven visibility, then apply only the
        // small protocol whitelist. Close, pin, title drag and window lifecycle controls are never
        // part of plugin-owned suppression.
        UpdateTopBarNewPaperButtons();

        if (_newTodoButton != null &&
            hidden.HasFlag(PaperHostTopBarActions.NewTodoPaper))
        {
            _newTodoButton.Visibility = Visibility.Collapsed;
        }
        if (_newNoteButton != null &&
            hidden.HasFlag(PaperHostTopBarActions.NewNotePaper))
        {
            _newNoteButton.Visibility = Visibility.Collapsed;
        }
    }
}
