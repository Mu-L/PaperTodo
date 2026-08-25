namespace PaperTodo;

internal enum ExperimentalVirtualDesktopWakeReason
{
    ShowOrBringToFront,
    CapsuleActivation
}

public sealed partial class AppController
{
    // Legacy call-site bridge only. PaperTodo no longer probes, tracks, or moves Windows virtual desktops.
    private void RefreshExperimentalVirtualDesktopRuntime()
    {
    }

    internal bool PreparePaperForCurrentVirtualDesktop(
        PaperWindow window,
        ExperimentalVirtualDesktopWakeReason reason)
    {
        return false;
    }

    private void DisposeExperimentalVirtualDesktopRuntime()
    {
    }
}
