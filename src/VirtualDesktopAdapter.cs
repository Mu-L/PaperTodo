namespace PaperTodo;

// Legacy compile-only type for surface helpers whose obsolete virtual-desktop methods
// are being removed. PaperTodo no longer probes or moves Windows virtual desktops.
internal sealed class VirtualDesktopAdapter
{
    public bool TryMoveWindowToDesktop(IntPtr hwnd, Guid desktopId) => false;
}
