namespace PaperTodo;

internal enum ExperimentalVirtualDesktopWakeReason
{
    ShowOrBringToFront,
    CapsuleActivation
}

public sealed partial class AppController
{
    private VirtualDesktopAdapter? _virtualDesktopAdapter;
    private VirtualDesktopProbeResult? _virtualDesktopProbe;

    private void RefreshExperimentalVirtualDesktopRuntime()
    {
        if (IsExiting ||
            !State.ExperimentalVirtualDesktopIntegration ||
            State.HidePapersFromWindowSwitcher)
        {
            DisposeExperimentalVirtualDesktopRuntime();
            return;
        }

        if (_virtualDesktopAdapter != null)
        {
            return;
        }

        var adapter = new VirtualDesktopAdapter();
        _virtualDesktopAdapter = adapter;
        _virtualDesktopProbe = adapter.Probe();
    }

    private void ToggleExperimentalVirtualDesktopIntegration()
    {
        if (State.HidePapersFromWindowSwitcher)
        {
            RefreshExperimentalVirtualDesktopRuntime();
            return;
        }

        State.ExperimentalVirtualDesktopIntegration =
            !State.ExperimentalVirtualDesktopIntegration;
        RefreshExperimentalVirtualDesktopRuntime();
        SaveNow();
        RefreshSettingsRegions("labs.virtualDesktop");
    }

    private void ToggleExperimentalVirtualDesktopMoveOnShow()
    {
        State.ExperimentalVirtualDesktopMoveOnShow =
            !State.ExperimentalVirtualDesktopMoveOnShow;
        SaveNow();
        RefreshSettingsRegions("labs.virtualDesktop");
    }

    private void ToggleExperimentalVirtualDesktopMoveOnCapsuleActivation()
    {
        State.ExperimentalVirtualDesktopMoveOnCapsuleActivation =
            !State.ExperimentalVirtualDesktopMoveOnCapsuleActivation;
        SaveNow();
        RefreshSettingsRegions("labs.virtualDesktop");
    }

    internal bool PreparePaperForCurrentVirtualDesktop(
        PaperWindow window,
        ExperimentalVirtualDesktopWakeReason reason)
    {
        if (IsExiting ||
            !State.ExperimentalVirtualDesktopIntegration ||
            State.HidePapersFromWindowSwitcher ||
            (reason == ExperimentalVirtualDesktopWakeReason.ShowOrBringToFront &&
             !State.ExperimentalVirtualDesktopMoveOnShow) ||
            (reason == ExperimentalVirtualDesktopWakeReason.CapsuleActivation &&
             !State.ExperimentalVirtualDesktopMoveOnCapsuleActivation))
        {
            return false;
        }

        RefreshExperimentalVirtualDesktopRuntime();
        var adapter = _virtualDesktopAdapter;
        if (adapter == null ||
            _virtualDesktopProbe?.IsUsable != true)
        {
            return false;
        }

        var handle = window.EnsureVirtualDesktopMainHandle();
        if (handle == IntPtr.Zero ||
            !adapter.TryIsWindowOnCurrentDesktop(
                handle,
                out var onCurrentDesktop))
        {
            return false;
        }
        if (onCurrentDesktop)
        {
            if (window.HasVirtualDesktopEdgeSurface &&
                adapter.TryGetCurrentDesktopId(
                    out var activeDesktopId))
            {
                MoveDeepCapsuleQueueToVirtualDesktop(
                    window,
                    adapter,
                    activeDesktopId);
            }
            return true;
        }

        if (!adapter.TryGetCurrentDesktopId(out var currentDesktopId) ||
            !window.TryMoveToVirtualDesktop(
                adapter,
                currentDesktopId))
        {
            return false;
        }
        MoveDeepCapsuleQueueToVirtualDesktop(
            window,
            adapter,
            currentDesktopId);

        return adapter.TryIsWindowOnCurrentDesktop(
                handle,
                out onCurrentDesktop) &&
            onCurrentDesktop;
    }

    private void MoveDeepCapsuleQueueToVirtualDesktop(
        PaperWindow activatedWindow,
        VirtualDesktopAdapter adapter,
        Guid desktopId)
    {
        if (!activatedWindow.HasVirtualDesktopEdgeSurface)
        {
            return;
        }

        var paper = State.Papers.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Id,
                activatedWindow.VirtualDesktopPaperId,
                StringComparison.Ordinal));
        if (paper == null)
        {
            return;
        }

        var queueKey = QueueKey(paper);
        foreach (var candidate in State.Papers)
        {
            if (QueueKey(candidate) == queueKey &&
                _windows.TryGetValue(
                    candidate.Id,
                    out var queueWindow) &&
                queueWindow.HasVirtualDesktopEdgeSurface)
            {
                queueWindow.MoveVirtualDesktopAuxiliarySurfaces(
                    adapter,
                    desktopId);
            }
        }

        if (_masterCapsules.TryGetValue(queueKey, out var master))
        {
            _ = master.TryMoveToVirtualDesktop(
                adapter,
                desktopId);
        }
    }

    private string ExperimentalVirtualDesktopStatusText()
    {
        if (State.HidePapersFromWindowSwitcher)
        {
            return Strings.Get(
                "LabsVirtualDesktopStatusWindowSwitcherConflict");
        }
        if (!State.ExperimentalVirtualDesktopIntegration)
        {
            return Strings.Get("LabsVirtualDesktopStatusOff");
        }

        return _virtualDesktopProbe?.IsUsable == true
            ? Strings.Get("LabsVirtualDesktopStatusReady")
            : Strings.Get("LabsVirtualDesktopStatusUnavailable");
    }

    private void DisposeExperimentalVirtualDesktopRuntime()
    {
        _virtualDesktopAdapter?.Dispose();
        _virtualDesktopAdapter = null;
        _virtualDesktopProbe = null;
    }
}
