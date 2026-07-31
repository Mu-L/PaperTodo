using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private ExternalWindowTracker? _externalWindowTracker;

    private bool NeedsExternalWindowTracker =>
        (State.ExperimentalCapsuleMagnetism &&
         State.ExperimentalCapsuleMagnetWindowEdges) ||
        State.ExperimentalWindowTethering;

    private void RefreshExperimentalWindowRuntime()
    {
        if (IsExiting)
        {
            DisposeExperimentalWindowRuntime();
            return;
        }

        if (NeedsExternalWindowTracker)
        {
            if (_externalWindowTracker == null)
            {
                var tracker = new ExternalWindowTracker(
                    Application.Current.Dispatcher);
                tracker.Changed += OnExternalWindowChanged;
                _externalWindowTracker = tracker;
            }
            return;
        }

        DisposeExternalWindowTracker();
    }

    private void OnExternalWindowChanged(ExternalWindowEvent windowEvent)
    {
        if (IsExiting || !NeedsExternalWindowTracker)
        {
            return;
        }

        foreach (var window in _windows.Values.ToList())
        {
            window.HandleExternalWindowEvent(windowEvent);
        }
    }

    private void ToggleExperimentalCapsuleMagnetism()
    {
        State.ExperimentalCapsuleMagnetism =
            !State.ExperimentalCapsuleMagnetism;
        if (!State.ExperimentalCapsuleMagnetism)
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.DisableExperimentalCapsuleMagnet();
            }
        }

        SaveNow();
        RefreshExperimentalWindowRuntime();
        RefreshSettingsWindowContent();
    }

    private void ToggleExperimentalCapsuleMagnetScreenEdges()
    {
        State.ExperimentalCapsuleMagnetScreenEdges =
            !State.ExperimentalCapsuleMagnetScreenEdges;
        foreach (var window in _windows.Values.ToList())
        {
            window.DisableExperimentalCapsuleMagnet();
        }
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleExperimentalCapsuleMagnetWindowEdges()
    {
        State.ExperimentalCapsuleMagnetWindowEdges =
            !State.ExperimentalCapsuleMagnetWindowEdges;
        foreach (var window in _windows.Values.ToList())
        {
            window.DisableExperimentalCapsuleMagnet();
        }
        SaveNow();
        RefreshExperimentalWindowRuntime();
        RefreshSettingsWindowContent();
    }

    private void SetExperimentalCapsuleMagnetDistance(int distance)
    {
        var normalized =
            ExperimentalWindowAttachmentOptions.NormalizeSnapDistance(
                distance);
        if (State.ExperimentalCapsuleMagnetDistance == normalized)
        {
            return;
        }

        State.ExperimentalCapsuleMagnetDistance = normalized;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleExperimentalWindowTethering()
    {
        State.ExperimentalWindowTethering =
            !State.ExperimentalWindowTethering;
        if (!State.ExperimentalWindowTethering)
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.DisableExperimentalWindowTether();
            }
        }

        SaveNow();
        RefreshExperimentalWindowRuntime();
        RefreshExperimentalAttachmentMenus();
        RefreshSettingsWindowContent();
    }

    private void SetExperimentalWindowTetherPreferredEdge(string edge)
    {
        var normalized = ExperimentalWindowTetherOptions.NormalizeEdge(edge);
        if (State.ExperimentalWindowTetherPreferredEdge == normalized)
        {
            return;
        }

        State.ExperimentalWindowTetherPreferredEdge = normalized;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalWindowTetherOptions();
        }
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void SetExperimentalWindowTetherGap(int gap)
    {
        var normalized = ExperimentalWindowTetherOptions.NormalizeGap(gap);
        if (State.ExperimentalWindowTetherGap == normalized)
        {
            return;
        }

        State.ExperimentalWindowTetherGap = normalized;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalWindowTetherOptions();
        }
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleExperimentalTetherVisibilityLink()
    {
        State.ExperimentalTetherVisibilityLink =
            !State.ExperimentalTetherVisibilityLink;
        if (!State.ExperimentalTetherVisibilityLink)
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.DisableExperimentalTetherVisibilityLink();
            }
        }
        else
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.RefreshExperimentalTetherVisibilityOptions();
            }
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void SetExperimentalTetherMinimizedBehavior(string behavior)
    {
        var normalized =
            ExperimentalTetherVisibilityModes.Normalize(behavior);
        if (State.ExperimentalTetherMinimizedBehavior == normalized)
        {
            return;
        }

        State.ExperimentalTetherMinimizedBehavior = normalized;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalTetherVisibilityOptions();
        }
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void RefreshExperimentalAttachmentMenus()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalAttachmentMenu();
        }
    }

    private void RefreshExperimentalAttachmentsAfterDisplayMetrics()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalAttachmentForDisplayMetrics();
        }
    }

    private void DisposeExternalWindowTracker()
    {
        if (_externalWindowTracker == null)
        {
            return;
        }

        _externalWindowTracker.Changed -= OnExternalWindowChanged;
        _externalWindowTracker.Dispose();
        _externalWindowTracker = null;
    }

    private void DisposeExperimentalWindowRuntime()
    {
        DisposeExternalWindowTracker();
        foreach (var window in _windows.Values.ToList())
        {
            window.DisposeExperimentalWindowAttachment();
        }
    }
}
