using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _experimentalFocusPresentationInitialized;

    internal void UpdateExperimentalFocusPresentationSettings()
    {
        InitializeExperimentalFocusPresentation();
        RefreshExperimentalFocusPresentation(animate: true);
    }

    private void InitializeExperimentalFocusPresentation()
    {
        if (_experimentalFocusPresentationInitialized)
        {
            return;
        }

        _experimentalFocusPresentationInitialized = true;
        MouseEnter += (_, _) => RefreshExperimentalFocusPresentation();
        MouseLeave += (_, _) => RefreshExperimentalFocusPresentation();
    }

    private void RefreshExperimentalFocusPresentation(bool animate = true)
    {
        if (!_isShellBuilt)
        {
            return;
        }

        var reveal =
            IsActive ||
            IsMouseOver ||
            HasOpenOwnedContextMenu() ||
            _titleBarDragSession != null ||
            _todoDrag?.IsDragging == true ||
            _topBarDrag?.IsDragging == true;

        if (_topBarHost != null)
        {
            var hideTitleBar =
                StateHidesInactiveTitleBar() &&
                !reveal;
            SetExperimentalVisualOpacity(
                _topBarHost,
                hideTitleBar ? 0.0 : 1.0,
                animate);
        }

        if (_topBarActionButtonsHost != null)
        {
            var hideButtons =
                _controller.State.ExperimentalHideInactiveTopBarButtons &&
                !reveal;
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
}
