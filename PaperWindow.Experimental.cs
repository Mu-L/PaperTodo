using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const int ExperimentalOpacityTransitionMilliseconds = 120;

    internal void UpdateExperimentalOpacitySettings(bool animate = true)
    {
        if (_isShellBuilt)
        {
            if (_controller.State.ExperimentalRestingCapsuleOpacity)
            {
                AttachCapsuleShellToExperimentalOpacityHost();
            }
            else
            {
                AttachCapsuleShellDirectlyToWindowHost();
            }
        }

        RefreshExperimentalOpacity(animate);
        if (_edgeCapsuleHost != null || HasDeepCapsuleSlotPlacement)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.State);
        }
    }

    private void RefreshExperimentalOpacity(bool animate = true)
    {
        if (!_isShellBuilt)
        {
            return;
        }

        var ownMenuOpen = HasOpenOwnedContextMenu();
        var expandedPaperInteractive =
            IsActive ||
            ownMenuOpen ||
            _titleBarDragSession != null ||
            _todoDrag?.IsDragging == true ||
            _noteLinkDrag?.IsDragging == true;
        var paperOpacity =
            !_controller.State.ExperimentalInactivePaperOpacity ||
            _paper.IsCollapsed ||
            expandedPaperInteractive
                ? 1.0
                : ExperimentalOpacityLevels.Normalize(
                    _controller.State.ExperimentalInactivePaperOpacityLevel,
                    ExperimentalOpacityLevels.DefaultInactivePaper);
        SetExperimentalVisualOpacity(_paperChrome, paperOpacity, animate);

        if (_controller.State.ExperimentalRestingCapsuleOpacity &&
            _capsuleOpacityHost != null)
        {
            var ordinaryCapsuleInteractive =
                _capsuleOpacityHost.IsMouseOver ||
                _capsulePointerState != CapsulePointerState.Idle ||
                ownMenuOpen;
            var capsuleOpacity =
                !_paper.IsCollapsed || ordinaryCapsuleInteractive
                    ? 1.0
                    : ExperimentalOpacityLevels.Normalize(
                        _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                        ExperimentalOpacityLevels.DefaultRestingCapsule);
            SetExperimentalVisualOpacity(
                _capsuleOpacityHost,
                capsuleOpacity,
                animate);
        }
    }

    private bool HasOpenOwnedContextMenu()
    {
        for (var i = _themedContextMenus.Count - 1; i >= 0; i--)
        {
            if (_themedContextMenus[i].TryGetTarget(out var menu))
            {
                if (menu.IsOpen)
                {
                    return true;
                }
            }
            else
            {
                _themedContextMenus.RemoveAt(i);
            }
        }

        return false;
    }

    private void SetExperimentalVisualOpacity(
        UIElement? element,
        double target,
        bool animate)
    {
        if (element == null)
        {
            return;
        }

        target = Math.Clamp(target, 0, 1);
        if (Math.Abs(element.Opacity - target) < 0.001)
        {
            return;
        }

        if (animate &&
            _controller.State.EnableAnimations &&
            IsVisible)
        {
            AnimationHelper.FadeTo(
                element,
                target,
                ExperimentalOpacityTransitionMilliseconds,
                AnimationHelper.QuickEase);
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = target;
    }
}
