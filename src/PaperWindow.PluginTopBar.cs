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
    private readonly List<(FrameworkElement Element, PaperTopBarActionScope Scope)>
        _pluginTopBarActionElements = [];
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
        _pluginTopBarActionElements.Clear();
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
            _pluginTopBarActionElements.Add((button, binding.Scope));
        }

        UpdatePluginTopBarHostWidth();
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
            // Host actions always win. Paper-scoped plugin actions are a small bounded group and
            // keep their existing all-or-nothing behavior. Global actions are unbounded at the API
            // layer, so after host + Paper fit, expose them one at a time in Priority order. The
            // first Global button that no longer fits is hidden and ends the pass; no controls are
            // compressed, reordered or removed to make room for it.
            foreach (var (element, _) in _pluginTopBarActionElements)
            {
                element.Visibility = Visibility.Collapsed;
            }
            UpdatePluginTopBarHostWidth();
            UpdateTopBarResponsiveLayout();

            if (_pluginTopBarActionElements.Count == 0 ||
                _topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                return;
            }

            foreach (var (element, scope) in _pluginTopBarActionElements)
            {
                if (scope == PaperTopBarActionScope.Paper)
                {
                    element.Visibility = Visibility.Visible;
                }
            }
            UpdatePluginTopBarHostWidth();
            UpdateTopBarResponsiveLayout();
            if (_topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                foreach (var (element, _) in _pluginTopBarActionElements)
                {
                    element.Visibility = Visibility.Collapsed;
                }
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                return;
            }

            foreach (var (element, scope) in _pluginTopBarActionElements)
            {
                if (scope != PaperTopBarActionScope.Global)
                {
                    continue;
                }

                element.Visibility = Visibility.Visible;
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                if (_topBarActionButtonsHost.Visibility == Visibility.Visible)
                {
                    continue;
                }

                element.Visibility = Visibility.Collapsed;
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                break;
            }
        }
        finally
        {
            _reconcilingPluginTopBarCapacity = false;
        }
    }

    private void UpdatePluginTopBarHostWidth()
    {
        if (_pluginTopBarButtonsHost == null)
        {
            return;
        }

        var width = _pluginTopBarActionElements
            .Where(item => item.Element.Visibility == Visibility.Visible)
            .Sum(item => TopBarOuterWidth(item.Element));
        _pluginTopBarButtonsHost.Width = width;
        _pluginTopBarButtonsHost.Visibility = width > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
