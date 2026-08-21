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
    private bool _pluginTopBarCapacityHookInstalled;
    private bool _reconcilingPluginHostActionVisibility;
    private bool _reconcilingPluginTopBarCapacity;

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
            button.SetBinding(
                Control.FontFamilyProperty,
                new Binding(nameof(FontFamily)) { Source = this });
            button.SetBinding(
                Control.FontSizeProperty,
                new Binding(nameof(FontSize)) { Source = this });
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

        // The host responsive algorithm can run while the entire right action group is Collapsed,
        // when a child's ActualWidth is no longer useful. Keep this aggregate width explicit so the
        // same TopBarOuterWidth calculation stays truthful in both visible and collapsed states.
        _pluginTopBarButtonsHost.Width = _pluginTopBarButtonsHost.Children
            .OfType<FrameworkElement>()
            .Sum(TopBarOuterWidth);

        _pluginHiddenHostTopBarActions = state.HiddenHostActions;
        ReconcilePluginHiddenHostTopBarActions();
        ReconcilePluginTopBarCapacity();
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

        EnsurePluginTopBarCapacityHook();
        EnsurePluginHostActionVisibilityHooks();
    }

    private void EnsurePluginTopBarCapacityHook()
    {
        if (_pluginTopBarCapacityHookInstalled || _topBar == null)
        {
            return;
        }

        _pluginTopBarCapacityHookInstalled = true;
        _topBar.SizeChanged += (_, _) => ReconcilePluginTopBarCapacity();
    }

    private void ReconcilePluginTopBarCapacity()
    {
        if (_reconcilingPluginTopBarCapacity ||
            _pluginTopBarButtonsHost == null ||
            _topBarActionButtonsHost == null ||
            _paper.IsCollapsed)
        {
            return;
        }

        _reconcilingPluginTopBarCapacity = true;
        try
        {
            // Host actions have absolute precedence over plugin priority. First ask the existing
            // responsive policy whether the host-only right group fits. Only if it does, try adding
            // the plugin group. If that would collapse the whole host action group, the plugin group
            // yields and the host-only decision is applied again. This keeps Global action count
            // unbounded without letting a plugin push PaperTodo's own controls out first.
            _pluginTopBarButtonsHost.Visibility = Visibility.Collapsed;
            UpdateTopBarResponsiveLayout();

            if (_pluginTopBarButtonsHost.Children.Count == 0 ||
                _topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                return;
            }

            _pluginTopBarButtonsHost.Visibility = Visibility.Visible;
            UpdateTopBarResponsiveLayout();
            if (_topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                _pluginTopBarButtonsHost.Visibility = Visibility.Collapsed;
                UpdateTopBarResponsiveLayout();
            }
        }
        finally
        {
            _reconcilingPluginTopBarCapacity = false;
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
        if (_reconcilingPluginHostActionVisibility)
        {
            return;
        }

        if (_pluginHiddenHostTopBarActions != PaperHostTopBarActions.None)
        {
            ReconcilePluginHiddenHostTopBarActions();
        }
        ReconcilePluginTopBarCapacity();
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
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            path.SetBinding(
                FrameworkElement.WidthProperty,
                new Binding(nameof(Control.FontSize)) { Source = button });
            path.SetBinding(
                FrameworkElement.HeightProperty,
                new Binding(nameof(Control.FontSize)) { Source = button });

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
