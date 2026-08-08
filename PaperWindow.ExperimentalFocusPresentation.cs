using System;
using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _experimentalFocusPresentationInitialized;
    private bool _experimentalInactiveTitleBarCollapsed;
    private double _experimentalInactiveTitleBarExtent;
    private double _experimentalInactiveTitleBarExpandedMinHeight;

    internal void UpdateExperimentalFocusPresentationSettings()
    {
        InitializeExperimentalFocusPresentation();
        RefreshExperimentalFocusPresentation(animate: true);
    }

    internal void RestoreExperimentalInactiveTitleBarGeometry()
    {
        ExpandExperimentalInactiveTitleBar();
    }

    private bool BeginExperimentalInactiveTitleBarLayoutChange()
    {
        if (!_experimentalInactiveTitleBarCollapsed)
        {
            return false;
        }

        ExpandExperimentalInactiveTitleBar();
        return true;
    }

    private void EndExperimentalInactiveTitleBarLayoutChange(bool reapply)
    {
        if (!reapply)
        {
            return;
        }

        _shell.UpdateLayout();
        RefreshExperimentalFocusPresentation(animate: false);
    }

    private void InitializeExperimentalFocusPresentation()
    {
        if (_experimentalFocusPresentationInitialized)
        {
            return;
        }

        _experimentalFocusPresentationInitialized = true;
        // Hover still reveals optional action buttons, but title-bar geometry follows focus.
        MouseEnter += (_, _) => RefreshExperimentalFocusPresentation();
        MouseLeave += (_, _) => RefreshExperimentalFocusPresentation();
    }

    private void RefreshExperimentalFocusPresentation(bool animate = true)
    {
        if (!_isShellBuilt)
        {
            return;
        }

        var interactionReveal =
            IsActive ||
            HasOpenOwnedContextMenu() ||
            _titleBarDragSession != null ||
            _todoDrag?.IsDragging == true ||
            _topBarDrag?.IsDragging == true;

        var hideTitleBar =
            StateHidesInactiveTitleBar() &&
            !interactionReveal &&
            CanUseExperimentalInactiveTitleBarGeometry();
        if (hideTitleBar)
        {
            CollapseExperimentalInactiveTitleBar();
        }
        else
        {
            ExpandExperimentalInactiveTitleBar();
        }

        if (_topBarActionButtonsHost != null)
        {
            var hideButtons =
                _controller.State.ExperimentalHideInactiveTopBarButtons &&
                !(interactionReveal || IsMouseOver);
            _topBarActionButtonsHost.IsHitTestVisible = !hideButtons;
            SetExperimentalVisualOpacity(
                _topBarActionButtonsHost,
                hideButtons ? 0.0 : 1.0,
                animate);
        }
    }

    private bool StateHidesInactiveTitleBar() =>
        _controller.State.ExperimentalHideInactiveTitleBar &&
        !_paper.IsCollapsed;

    private bool CanUseExperimentalInactiveTitleBarGeometry()
    {
        return IsVisible &&
            !_paper.IsCollapsed &&
            WindowState == WindowState.Normal &&
            !_isSnappedPresentation &&
            !IsPaperFormTransitioning &&
            !HasDeepCapsuleSlotPlacement;
    }

    private double ExperimentalTitleBarExtent()
    {
        if (_shell.RowDefinitions.Count > 0 &&
            _shell.RowDefinitions[0].ActualHeight > 0.5)
        {
            return _shell.RowDefinitions[0].ActualHeight;
        }

        if (_topBarHost != null)
        {
            var measured =
                _topBarHost.ActualHeight +
                _topBarHost.Margin.Top +
                _topBarHost.Margin.Bottom;
            if (measured > 0.5)
            {
                return measured;
            }

            return Math.Max(
                1,
                TitleBarHeight +
                _topBarHost.BorderThickness.Top +
                _topBarHost.BorderThickness.Bottom +
                _topBarHost.Margin.Top +
                _topBarHost.Margin.Bottom);
        }

        return Math.Max(1, TitleBarHeight);
    }

    private void CollapseExperimentalInactiveTitleBar()
    {
        if (_experimentalInactiveTitleBarCollapsed ||
            _topBarHost == null ||
            _shell.RowDefinitions.Count == 0)
        {
            return;
        }

        var currentHeight =
            double.IsFinite(Height) && Height > 0
                ? Height
                : ActualHeight;
        if (!double.IsFinite(Top) ||
            !double.IsFinite(currentHeight) ||
            currentHeight <= 1)
        {
            return;
        }

        var extent = Math.Min(
            ExperimentalTitleBarExtent(),
            Math.Max(1, currentHeight - 1));
        var bottom = Top + currentHeight;
        var targetHeight = Math.Max(1, currentHeight - extent);
        var targetTop = bottom - targetHeight;

        _experimentalInactiveTitleBarExtent = extent;
        _experimentalInactiveTitleBarExpandedMinHeight = MinHeight;
        _experimentalInactiveTitleBarCollapsed = true;

        _topBarHost.BeginAnimation(OpacityProperty, null);
        _topBarHost.Opacity = 0;
        _topBarHost.IsHitTestVisible = false;
        _topBarHost.Visibility = Visibility.Collapsed;
        _shell.RowDefinitions[0].Height = new GridLength(0);
        _shell.UpdateLayout();

        MoveWindowWithoutGeometrySave(() =>
        {
            MinHeight = Math.Max(
                1,
                _experimentalInactiveTitleBarExpandedMinHeight - extent);
            Top = RoundToDevicePixelY(targetTop);
            Height = RoundToDevicePixelY(targetHeight);
        });
    }

    private void ExpandExperimentalInactiveTitleBar()
    {
        if (!_experimentalInactiveTitleBarCollapsed)
        {
            if (_topBarHost != null)
            {
                _topBarHost.BeginAnimation(OpacityProperty, null);
                _topBarHost.Opacity = 1;
                _topBarHost.Visibility = Visibility.Visible;
                _topBarHost.IsHitTestVisible = true;
            }
            return;
        }

        var extent = Math.Max(1, _experimentalInactiveTitleBarExtent);
        var currentHeight =
            double.IsFinite(Height) && Height > 0
                ? Height
                : ActualHeight;
        if (!double.IsFinite(Top) ||
            !double.IsFinite(currentHeight) ||
            currentHeight <= 0)
        {
            return;
        }

        if (_topBarHost != null)
        {
            _topBarHost.Visibility = Visibility.Visible;
            _topBarHost.IsHitTestVisible = true;
        }
        if (_shell.RowDefinitions.Count > 0)
        {
            _shell.RowDefinitions[0].Height = GridLength.Auto;
        }
        _shell.UpdateLayout();

        var bottom = Top + currentHeight;
        var targetHeight = currentHeight + extent;
        var targetTop = bottom - targetHeight;
        MoveWindowWithoutGeometrySave(() =>
        {
            Top = RoundToDevicePixelY(targetTop);
            Height = RoundToDevicePixelY(targetHeight);
            MinHeight = Math.Max(
                1,
                _experimentalInactiveTitleBarExpandedMinHeight);
        });

        if (_topBarHost != null)
        {
            _topBarHost.BeginAnimation(OpacityProperty, null);
            _topBarHost.Opacity = 1;
        }

        _experimentalInactiveTitleBarCollapsed = false;
        _experimentalInactiveTitleBarExtent = 0;
        _experimentalInactiveTitleBarExpandedMinHeight = 0;
    }
}
