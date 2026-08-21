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
    private PaperHostTopBarActions _pluginHiddenHostTopBarActions;
    private bool _pluginHostActionVisibilityHooksInstalled;
    private bool _reconcilingPluginHostActionVisibility;
    private bool _pluginTopBarTypographyHookInstalled;
    private double _pluginTopBarAppliedScale = double.NaN;
    private string _pluginTopBarAppliedFontFamily = string.Empty;

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
            button.Opacity = binding.Action.Enabled ? 1.0 : 0.5;
            button.Width = 23;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Content = CreatePluginTopBarIcon(button, binding.Action.Icon);
            button.Click += (_, _) =>
                _controller.InvokePluginTopBarAction(
                    binding,
                    _paper.Id,
                    _paper.Type,
                    _paper.Type == PaperTypes.Note
                        ? NormalizeBodyProviderId(_paper.BodyProviderId)
                        : string.Empty);
            _pluginTopBarButtonsHost.Children.Add(button);
        }

        _pluginTopBarAppliedScale = AppTypography.ScaleFactor;
        _pluginTopBarAppliedFontFamily = AppTypography.UiFontFamily.Source;
        _pluginHiddenHostTopBarActions = state.HiddenHostActions;
        ReconcilePluginHiddenHostTopBarActions();
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

        EnsurePluginHostActionVisibilityHooks();
        if (!_pluginTopBarTypographyHookInstalled)
        {
            _pluginTopBarTypographyHookInstalled = true;
            LayoutUpdated += OnPluginTopBarLayoutUpdated;
        }
    }

    private void EnsurePluginHostActionVisibilityHooks()
    {
        if (_pluginHostActionVisibilityHooksInstalled ||
            _newTodoButton == null ||
            _newNoteButton == null)
        {
            return;
        }

        _pluginHostActionVisibilityHooksInstalled = true;
        _newTodoButton.IsVisibleChanged += OnPluginHostActionVisibilityChanged;
        _newNoteButton.IsVisibleChanged += OnPluginHostActionVisibilityChanged;
    }

    private void OnPluginHostActionVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_reconcilingPluginHostActionVisibility ||
            _pluginHiddenHostTopBarActions == PaperHostTopBarActions.None)
        {
            return;
        }

        ReconcilePluginHiddenHostTopBarActions();
    }

    private void OnPluginTopBarLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pluginTopBarButtonsHost == null ||
            _pluginTopBarButtonsHost.Children.Count == 0)
        {
            return;
        }

        var scale = AppTypography.ScaleFactor;
        var fontFamily = AppTypography.UiFontFamily.Source;
        if (Math.Abs(scale - _pluginTopBarAppliedScale) <= 0.001 &&
            string.Equals(
                fontFamily,
                _pluginTopBarAppliedFontFamily,
                StringComparison.Ordinal))
        {
            return;
        }

        RefreshPluginTopBarActions();
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

            if (icon.RenderMode == PaperTopBarSvgRenderMode.Stroke)
            {
                path.Fill = Brushes.Transparent;
                path.StrokeThickness = icon.StrokeWidth;
                path.StrokeLineJoin = PenLineJoin.Round;
                path.StrokeStartLineCap = PenLineCap.Round;
                path.StrokeEndLineCap = PenLineCap.Round;
                path.SetBinding(
                    Shape.StrokeProperty,
                    new Binding(nameof(Control.Foreground)) { Source = button });
            }
            else
            {
                path.SetBinding(
                    Shape.FillProperty,
                    new Binding(nameof(Control.Foreground)) { Source = button });
            }
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

    private void ReconcilePluginHiddenHostTopBarActions()
    {
        if (_reconcilingPluginHostActionVisibility)
        {
            return;
        }

        _reconcilingPluginHostActionVisibility = true;
        try
        {
            // The user setting is the base visibility; plugin suppression is the final paper-local
            // layer. Reapplying this from the visibility hooks prevents a later settings refresh
            // from temporarily resurrecting actions that the active provider asked to hide.
            UpdateTopBarNewPaperButtons();

            if (_newTodoButton != null &&
                _pluginHiddenHostTopBarActions.HasFlag(
                    PaperHostTopBarActions.NewTodoPaper))
            {
                _newTodoButton.Visibility = Visibility.Collapsed;
            }
            if (_newNoteButton != null &&
                _pluginHiddenHostTopBarActions.HasFlag(
                    PaperHostTopBarActions.NewNotePaper))
            {
                _newNoteButton.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _reconcilingPluginHostActionVisibility = false;
        }
    }
}
