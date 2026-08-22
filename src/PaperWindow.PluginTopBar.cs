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
    private PluginTopBarActionBinding[] _pluginTopBarDesiredActions = [];
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

        // Keep descriptors cheap. WPF controls are materialized only for the currently fitting
        // prefix, so an app runtime cannot multiply thousands of hidden Buttons across papers.
        _pluginTopBarDesiredActions = state.Actions
            .Where(binding => binding.Action.Visible)
            .ToArray();
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
            _topBarActionButtonsHost == null)
        {
            return;
        }

        _reconcilingPluginTopBarCapacity = true;
        try
        {
            // Rebuild only the visible prefix. Paper actions are bounded and keep their existing
            // all-or-nothing behavior. Global descriptors are already in Priority order; create
            // them one at a time and stop as soon as the next one would displace host controls.
            _pluginTopBarButtonsHost.Children.Clear();
            _pluginTopBarActionElements.Clear();
            UpdatePluginTopBarHostWidth();
            UpdateTopBarResponsiveLayout();

            if (_paper.IsCollapsed ||
                _pluginTopBarDesiredActions.Length == 0 ||
                _topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                return;
            }

            foreach (var binding in _pluginTopBarDesiredActions.Where(item =>
                         item.Scope == PaperTopBarActionScope.Paper))
            {
                AddPluginTopBarActionElement(binding);
            }
            UpdatePluginTopBarHostWidth();
            UpdateTopBarResponsiveLayout();
            if (_topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                _pluginTopBarButtonsHost.Children.Clear();
                _pluginTopBarActionElements.Clear();
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                return;
            }

            foreach (var binding in _pluginTopBarDesiredActions.Where(item =>
                         item.Scope == PaperTopBarActionScope.Global))
            {
                var element = AddPluginTopBarActionElement(binding);
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                if (_topBarActionButtonsHost.Visibility == Visibility.Visible)
                {
                    continue;
                }

                _pluginTopBarButtonsHost.Children.Remove(element);
                _pluginTopBarActionElements.RemoveAt(
                    _pluginTopBarActionElements.Count - 1);
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

    private FrameworkElement AddPluginTopBarActionElement(
        PluginTopBarActionBinding binding)
    {
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
        _pluginTopBarButtonsHost!.Children.Add(button);
        _pluginTopBarActionElements.Add((button, binding.Scope));
        return button;
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
