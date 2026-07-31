namespace PaperTodo;

public sealed partial class AppController
{
    private PaperWindow? _lastActivatedPaperWindow;
    private PaperWindow? _experimentalCurrentPassiveWindow;

    internal void NotifyPaperWindowActivated(PaperWindow window)
    {
        if (window.CanEnterCurrentExperimentalPassive &&
            !window.IsExperimentalPassive)
        {
            _lastActivatedPaperWindow = window;
        }
    }

    internal void NotifyPaperWindowClosed(PaperWindow window)
    {
        if (ReferenceEquals(_lastActivatedPaperWindow, window))
        {
            _lastActivatedPaperWindow = null;
        }
        if (ReferenceEquals(_experimentalCurrentPassiveWindow, window))
        {
            _experimentalCurrentPassiveWindow = null;
        }
    }

    private void ExecuteExperimentalShortcut(GlobalShortcutDefinition definition)
    {
        if (!State.GlobalHotkeyEnabled.GetValueOrDefault(definition.Id))
        {
            return;
        }

        switch (definition.ExperimentalKind)
        {
            case ExperimentalShortcutKind.CurrentPaperPassive:
                ToggleCurrentPaperExperimentalPassive();
                break;
        }
    }

    private void ToggleCurrentPaperExperimentalPassive()
    {
        if (_experimentalCurrentPassiveWindow is { } passiveWindow)
        {
            passiveWindow.SetExperimentalPassiveReason(
                ExperimentalPassiveReason.CurrentPaper,
                enabled: false);
            _experimentalCurrentPassiveWindow = null;
            RefreshTrayMenu();
            return;
        }

        var target = _windows.Values.FirstOrDefault(window =>
                window.IsActive &&
                window.CanEnterCurrentExperimentalPassive) ??
            (_lastActivatedPaperWindow is { } lastWindow &&
             _windows.Values.Contains(lastWindow) &&
             lastWindow.CanEnterCurrentExperimentalPassive
                ? lastWindow
                : null);
        if (target == null)
        {
            return;
        }

        target.SetExperimentalPassiveReason(
            ExperimentalPassiveReason.CurrentPaper,
            enabled: true);
        _experimentalCurrentPassiveWindow = target;
        RefreshTrayMenu();
    }

    private void HandleExperimentalShortcutFeatureChanged(
        GlobalShortcutDefinition definition,
        bool enabled)
    {
        if (enabled)
        {
            return;
        }

        if (definition.ExperimentalKind == ExperimentalShortcutKind.CurrentPaperPassive)
        {
            RestoreCurrentPaperExperimentalPassive();
        }
    }

    private void RestoreCurrentPaperExperimentalPassive()
    {
        if (_experimentalCurrentPassiveWindow is not { } window)
        {
            return;
        }

        window.SetExperimentalPassiveReason(
            ExperimentalPassiveReason.CurrentPaper,
            enabled: false);
        _experimentalCurrentPassiveWindow = null;
        RefreshTrayMenu();
    }

    private void RestoreExperimentalPassiveForWindow(PaperWindow window)
    {
        window.SetExperimentalPassiveReason(
            ExperimentalPassiveReason.CurrentPaper,
            enabled: false);
        if (ReferenceEquals(_experimentalCurrentPassiveWindow, window))
        {
            _experimentalCurrentPassiveWindow = null;
        }
    }

    private bool HasExperimentalPassiveSurfaces =>
        _experimentalCurrentPassiveWindow != null;

    private void RestoreAllExperimentalPassiveSurfaces()
    {
        RestoreCurrentPaperExperimentalPassive();
    }
}
