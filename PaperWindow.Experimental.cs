using System.Windows;

namespace PaperTodo;

[Flags]
internal enum ExperimentalPassiveReason
{
    None = 0,
    CurrentPaper = 1,
    AllSurfaces = 2
}

public sealed partial class PaperWindow
{
    private const int ExperimentalOpacityTransitionMilliseconds = 120;
    private ExperimentalPassiveReason _experimentalPassiveReasons;
    private bool _experimentalPassiveNativeApplied;
    private bool _experimentalPassiveHadNoActivateStyle;

    internal bool IsExperimentalPassive =>
        _experimentalPassiveReasons != ExperimentalPassiveReason.None;

    private bool IsExperimentalAllSurfacesPassive =>
        (_experimentalPassiveReasons & ExperimentalPassiveReason.AllSurfaces) != 0;

    internal bool CanEnterCurrentExperimentalPassive =>
        !IsClosed &&
        IsVisible &&
        WindowState != WindowState.Minimized;

    internal void SetExperimentalPassiveReason(
        ExperimentalPassiveReason reason,
        bool enabled)
    {
        if (reason == ExperimentalPassiveReason.None)
        {
            return;
        }

        var previous = _experimentalPassiveReasons;
        _experimentalPassiveReasons = enabled
            ? previous | reason
            : previous & ~reason;
        if (previous == _experimentalPassiveReasons)
        {
            return;
        }

        if (_experimentalPassiveReasons != ExperimentalPassiveReason.None)
        {
            AbortAllInteractions(InteractionAbortReason.Deactivated);
            if (IsActive)
            {
                WindowNative.ClearCurrentThreadKeyboardFocus();
            }
        }

        ApplyExperimentalPassiveNativeState();
        RefreshEffectiveTopmost();
    }

    internal void SetExperimentalAllSurfacesPassive(bool enabled)
    {
        _edgeCapsuleHost?.SetExperimentalPassive(enabled);
        _experimentalTetherCapsule?.SetExperimentalPassive(enabled);
        SetExperimentalPassiveReason(
            ExperimentalPassiveReason.AllSurfaces,
            enabled);
    }

    private void ApplyExperimentalPassiveNativeState()
    {
        var passive = _experimentalPassiveReasons != ExperimentalPassiveReason.None;
        if (passive)
        {
            if (!_experimentalPassiveNativeApplied)
            {
                _experimentalPassiveHadNoActivateStyle =
                    WindowNative.HasNoActivateStyle(this);
            }

            WindowNative.SetNoActivateStyle(this, enabled: true);
            WindowNative.SetInputPassthrough(this, enabled: true);
            _experimentalPassiveNativeApplied = true;
            return;
        }

        if (!_experimentalPassiveNativeApplied)
        {
            return;
        }

        WindowNative.SetInputPassthrough(this, enabled: false);
        if (!_experimentalPassiveHadNoActivateStyle)
        {
            WindowNative.SetNoActivateStyle(this, enabled: false);
        }

        _experimentalPassiveNativeApplied = false;
        _experimentalPassiveHadNoActivateStyle = false;
    }

    internal void UpdateExperimentalOpacitySettings(bool animate = true)
    {
        _experimentalTetherCapsule?.UpdateRestingOpacity(
            _controller.State.ExperimentalRestingCapsuleOpacity
                ? ExperimentalOpacityLevels.Normalize(
                    _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                    ExperimentalOpacityLevels.DefaultRestingCapsule)
                : 1.0);
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
