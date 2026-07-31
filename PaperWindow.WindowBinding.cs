using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private sealed class WindowBindingDragState
    {
        public WindowBindingDragState(
            FrameworkElement handle,
            DeviceScreenPoint startScreenPoint)
        {
            Handle = handle;
            StartScreenPoint = startScreenPoint;
        }

        public FrameworkElement Handle { get; }
        public DeviceScreenPoint StartScreenPoint { get; }
        public bool IsDragging { get; set; }
        public bool SuppressCaptureLossEnd { get; set; }
        public Window? Ghost { get; set; }
        public Border? GhostChrome { get; set; }
        public TextBlock? GhostLabel { get; set; }
        public ExternalWindowSnapshot? Target { get; set; }
        public IntPtr FullscreenAvoidanceWindow { get; set; }
    }

    private Button? _windowBindingButton;
    private WindowBindingDragState? _windowBindingDrag;

    private void ConfigureWindowBindingButton(Button button)
    {
        _windowBindingButton = button;
        button.Width = 24;
        button.FontSize = AppTypography.Scale(13);
        button.Cursor = Cursors.Cross;
        button.PreviewMouseLeftButtonDown +=
            (_, e) => BeginWindowBindingMouseGesture(button, e);
        button.PreviewMouseMove +=
            (_, e) => UpdateWindowBindingMouseGesture(e);
        button.PreviewMouseLeftButtonUp +=
            (_, e) => EndWindowBindingMouseGestureFromMouseUp(e);
        button.PreviewMouseRightButtonUp +=
            (_, e) => OpenWindowBindingButtonMenu(button, e);
        button.LostMouseCapture += (_, _) =>
        {
            var state = _windowBindingDrag;
            if (state?.SuppressCaptureLossEnd == true)
            {
                return;
            }

            if (state != null &&
                Mouse.LeftButton == MouseButtonState.Pressed &&
                state.Handle.IsVisible &&
                state.Handle.IsEnabled)
            {
                state.Handle.CaptureMouse();
                return;
            }

            EndWindowBindingMouseGesture(commit: false);
        };
        RefreshWindowBindingButton();
    }

    private void RefreshWindowBindingButton()
    {
        if (_windowBindingButton == null)
        {
            return;
        }

        var enabled =
            _controller.State.ExperimentalWindowTethering;
        _windowBindingButton.Visibility =
            enabled ? Visibility.Visible : Visibility.Collapsed;
        if (HasExperimentalWindowTether)
        {
            _windowBindingButton.Foreground = Theme.ActiveBrush;
        }
        else
        {
            _windowBindingButton.ClearValue(Control.ForegroundProperty);
        }
        _windowBindingButton.ToolTip = HasExperimentalWindowTether &&
            _experimentalWindowAttachment is { } session
                ? Strings.Format(
                    "ToolTipWindowBindingActiveFormat",
                    session.TargetTitle)
                : Strings.Get("ToolTipDragPaperToWindow");
    }

    private void OpenWindowBindingButtonMenu(
        FrameworkElement placementTarget,
        MouseButtonEventArgs e)
    {
        if (!HasExperimentalWindowTether)
        {
            return;
        }

        var menu = CreateContextMenu();
        menu.Items.Add(MenuItem(
            Strings.Get("LabsWindowTetherDetach"),
            (_, _) => DetachExperimentalWindowAttachment(
                savePosition: true)));
        var previousContextMenu = placementTarget.ContextMenu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(placementTarget.ContextMenu, menu))
            {
                placementTarget.ContextMenu = previousContextMenu;
            }
        };
        placementTarget.ContextMenu = menu;
        menu.PlacementTarget = placementTarget;
        menu.Placement =
            System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void BeginWindowBindingMouseGesture(
        FrameworkElement handle,
        MouseButtonEventArgs e)
    {
        if (!_controller.State.ExperimentalWindowTethering ||
            _paper.IsCollapsed ||
            IsPaperFormTransitioning ||
            WindowState != System.Windows.WindowState.Normal ||
            _isSnappedPresentation)
        {
            return;
        }

        EndWindowBindingMouseGesture(commit: false);
        _windowBindingDrag = new WindowBindingDragState(
            handle,
            DeviceScreenPoint.FromPoint(
                PointToScreen(e.GetPosition(this))));
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateWindowBindingMouseGesture(MouseEventArgs e)
    {
        var state = _windowBindingDrag;
        if (state == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndWindowBindingMouseGesture(commit: false);
            e.Handled = true;
            return;
        }

        var currentScreenPoint = DeviceScreenPoint.FromPoint(
            PointToScreen(e.GetPosition(this)));
        if (!state.IsDragging)
        {
            if (!WindowWorkAreaHelper.ExceedsDragThreshold(
                    state.StartScreenPoint,
                    currentScreenPoint,
                    this))
            {
                return;
            }

            state.IsDragging = true;
            state.Handle.Opacity = 0.72;
            Mouse.OverrideCursor = Cursors.Cross;
            ExitNoteEditor();

            state.SuppressCaptureLossEnd = true;
            try
            {
                state.Ghost = CreateWindowBindingDragGhost(
                    out var ghostChrome,
                    out var ghostLabel);
                state.GhostChrome = ghostChrome;
                state.GhostLabel = ghostLabel;
                state.Ghost.Show();
                state.Ghost.UpdateLayout();
                if (Mouse.LeftButton == MouseButtonState.Pressed &&
                    !state.Handle.IsMouseCaptured)
                {
                    state.Handle.CaptureMouse();
                }
            }
            catch
            {
                CloseWindowBindingDragGhost(state);
                EndWindowBindingMouseGesture(commit: false);
                e.Handled = true;
                return;
            }
            finally
            {
                state.SuppressCaptureLossEnd = false;
                if (_windowBindingDrag == state &&
                    Mouse.LeftButton == MouseButtonState.Pressed &&
                    !state.Handle.IsMouseCaptured)
                {
                    state.Handle.CaptureMouse();
                }
            }

            if (_windowBindingDrag != state || state.Ghost == null)
            {
                e.Handled = true;
                return;
            }
        }

        UpdateWindowBindingDragTarget(state, currentScreenPoint);
        MoveWindowBindingDragGhost(state, currentScreenPoint);
        e.Handled = true;
    }

    private void EndWindowBindingMouseGestureFromMouseUp(
        MouseButtonEventArgs e)
    {
        var state = _windowBindingDrag;
        if (state == null)
        {
            return;
        }

        if (state.IsDragging)
        {
            var point = DeviceScreenPoint.FromPoint(
                PointToScreen(e.GetPosition(this)));
            UpdateWindowBindingDragTarget(state, point);
        }
        EndWindowBindingMouseGesture(commit: state.IsDragging);
        e.Handled = true;
    }

    private void EndWindowBindingMouseGesture(bool commit)
    {
        var state = _windowBindingDrag;
        if (state == null || state.SuppressCaptureLossEnd)
        {
            return;
        }

        _windowBindingDrag = null;
        if (state.Handle.IsMouseCaptured)
        {
            state.Handle.ReleaseMouseCapture();
        }

        var target = commit ? state.Target : null;
        CloseWindowBindingDragGhost(state);
        state.Handle.Opacity = 1.0;
        Mouse.OverrideCursor = null;

        if (target is { } selected)
        {
            var attached =
                AttachExperimentalWindowTether(selected.Identity);
            if (attached &&
                _controller.State.EnableAnimations &&
                _windowBindingButton != null)
            {
                AnimationHelper.QuickBounce(
                    _windowBindingButton,
                    scale: 1.16,
                    duration: 90);
            }
        }
        RefreshWindowBindingButton();
    }

    private void UpdateWindowBindingDragTarget(
        WindowBindingDragState state,
        DeviceScreenPoint point)
    {
        state.Target = ExternalWindowNative.TryGetTargetAtPoint(
                point,
                out var target)
            ? target
            : null;
        if (state.GhostChrome == null || state.GhostLabel == null)
        {
            return;
        }

        if (state.Target is { } selected)
        {
            state.GhostChrome.BorderBrush = Theme.ActiveBrush;
            state.GhostChrome.Background = Theme.Tint(
                (byte)(Theme.IsDark ? 52 : 34));
            state.GhostLabel.Foreground = TextBrush;
            state.GhostLabel.Text = Strings.Format(
                "WindowBindingDropTargetFormat",
                EllipsizeWindowBindingTarget(selected.Title));
            return;
        }

        state.GhostChrome.BorderBrush = PaperBorderBrush;
        state.GhostChrome.Background = PaperBrush;
        state.GhostLabel.Foreground = WeakTextBrush;
        state.GhostLabel.Text = Strings.Get("WindowBindingDragHint");
    }

    private Window CreateWindowBindingDragGhost(
        out Border chrome,
        out TextBlock label)
    {
        label = new TextBlock
        {
            Text = Strings.Get("WindowBindingDragHint"),
            Foreground = WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            MaxWidth = AppTypography.Scale(240),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new TextBlock
        {
            Text = "▣",
            Foreground = Theme.ActiveBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            IsHitTestVisible = false
        };
        content.Children.Add(icon);
        content.Children.Add(label);

        chrome = new Border
        {
            Padding = new Thickness(10, 6, 11, 6),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = PaperBrush,
            BorderBrush = PaperBorderBrush,
            BorderThickness = new Thickness(1.2),
            Opacity = 0.94,
            Child = content,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = chrome
        };
        window.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(window);
            WindowNative.SetInputPassthrough(window, enabled: true);
        };
        AppTypography.ApplyTextRendering(window);
        return window;
    }

    private void MoveWindowBindingDragGhost(
        WindowBindingDragState state,
        DeviceScreenPoint point)
    {
        var ghost = state.Ghost;
        if (ghost == null)
        {
            return;
        }

        try
        {
            if (!WindowNative.TryGetWindowDeviceBounds(
                    ghost,
                    out _))
            {
                return;
            }

            _ = WindowNative.TryMoveWindowDevicePosition(
                ghost,
                new DeviceScreenPoint(
                    point.X + 14,
                    point.Y + 18));
            RefreshWindowBindingDragGhostTopmost(state);
        }
        catch
        {
            // Drag feedback is disposable UI.
        }
    }

    private void RefreshWindowBindingDragGhostTopmost(
        WindowBindingDragState state)
    {
        var ghost = state.Ghost;
        if (ghost == null)
        {
            return;
        }

        var avoidanceWindow =
            _controller.FullscreenAvoidanceWindowFor(ghost);
        if (state.FullscreenAvoidanceWindow == avoidanceWindow)
        {
            return;
        }

        state.FullscreenAvoidanceWindow = avoidanceWindow;
        var topmost = avoidanceWindow == IntPtr.Zero;
        ghost.Topmost = topmost;
        if (ghost.IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(
                ghost,
                topmost,
                avoidanceWindow);
        }
    }

    private static void CloseWindowBindingDragGhost(
        WindowBindingDragState state)
    {
        if (state.Ghost == null)
        {
            return;
        }

        try
        {
            state.Ghost.Close();
        }
        catch
        {
            // Drag feedback is disposable UI.
        }

        state.Ghost = null;
        state.GhostChrome = null;
        state.GhostLabel = null;
        state.Target = null;
    }

    private static string EllipsizeWindowBindingTarget(string title) =>
        title.Length <= 52 ? title : title[..49] + "…";
}
