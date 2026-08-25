namespace PaperTodo;

internal enum ExperimentalVirtualDesktopWakeReason
{
    ShowOrBringToFront,
    CapsuleActivation
}

public sealed partial class AppController
{
    // Transitional compile bridge while obsolete virtual-desktop call sites are removed.
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
