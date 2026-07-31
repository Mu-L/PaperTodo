using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private sealed record WindowBindingDragFeedback(
        Border Chrome,
        TextBlock Label);

    private Button? _windowBindingButton;
    private ExternalWindowSnapshot? _windowBindingDragTarget;

    private void ConfigureWindowBindingButton(Button button)
    {
        _windowBindingButton = button;
        button.Width = 24;
        button.FontSize = AppTypography.Scale(13);
        button.Cursor = Cursors.Cross;
        button.PreviewMouseRightButtonUp +=
            (_, e) => OpenWindowBindingButtonMenu(button, e);
        ConfigureTopBarDragGesture(
            button,
            new TopBarDragBehavior
            {
                Kind = TopBarDragKind.WindowBinding,
                CanBegin = CanBeginWindowBindingDrag,
                Started = () =>
                {
                    _windowBindingDragTarget = null;
                    ExitNoteEditor();
                },
                CreateFeedback = CreateWindowBindingDragFeedback,
                Moved = UpdateWindowBindingDragTarget,
                Completed = CompleteWindowBindingDrag,
                GhostPlacement = TopBarDragGhostPlacement.PointerOffset,
                DraggingOpacity = 0.72
            });
        RefreshWindowBindingButton();
    }

    private bool CanBeginWindowBindingDrag() =>
        _controller.State.ExperimentalWindowTethering &&
        !_paper.IsCollapsed &&
        !IsPaperFormTransitioning &&
        WindowState == System.Windows.WindowState.Normal &&
        !_isSnappedPresentation;

    private void RefreshWindowBindingButton()
    {
        if (_windowBindingButton == null)
        {
            return;
        }

        var enabled = _controller.State.ExperimentalWindowTethering;
        var isBound = HasExperimentalWindowTether;
        _windowBindingButton.Visibility =
            enabled ? Visibility.Visible : Visibility.Collapsed;
        _windowBindingButton.Content = isBound ? "◉" : "◎";
        _windowBindingButton.FontWeight =
            isBound ? FontWeights.Bold : FontWeights.SemiBold;
        if (isBound)
        {
            _windowBindingButton.Foreground = Theme.ActiveBrush;
        }
        else
        {
            _windowBindingButton.ClearValue(Control.ForegroundProperty);
        }
        _windowBindingButton.ToolTip = isBound &&
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

    private void UpdateWindowBindingDragTarget(
        TopBarDragFeedback feedback,
        DeviceScreenPoint point)
    {
        _windowBindingDragTarget =
            ExternalWindowNative.TryGetTargetAtPoint(
                point,
                out var target)
                ? target
                : null;
        if (feedback.Context is not WindowBindingDragFeedback visual)
        {
            return;
        }

        if (_windowBindingDragTarget is { } selected)
        {
            visual.Chrome.BorderBrush = Theme.ActiveBrush;
            visual.Chrome.Background = Theme.Tint(
                (byte)(Theme.IsDark ? 52 : 34));
            visual.Label.Foreground = TextBrush;
            visual.Label.Text = Strings.Format(
                "WindowBindingDropTargetFormat",
                EllipsizeWindowBindingTarget(selected.Title));
            return;
        }

        visual.Chrome.BorderBrush = PaperBorderBrush;
        visual.Chrome.Background = PaperBrush;
        visual.Label.Foreground = WeakTextBrush;
        visual.Label.Text = Strings.Get("WindowBindingDragHint");
    }

    private void CompleteWindowBindingDrag(bool commit)
    {
        var target = commit ? _windowBindingDragTarget : null;
        _windowBindingDragTarget = null;
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

    private TopBarDragFeedback CreateWindowBindingDragFeedback()
    {
        var label = new TextBlock
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
            Text = "◎",
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

        var chrome = new Border
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
        return new TopBarDragFeedback(
            CreateTopBarDragFeedbackWindow(chrome),
            new WindowBindingDragFeedback(chrome, label));
    }

    private static string EllipsizeWindowBindingTarget(string title) =>
        title.Length <= 52 ? title : title[..49] + "…";
}
