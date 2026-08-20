using System.Windows.Interop;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal string VirtualDesktopPaperId => _paper.Id;
    internal bool HasVirtualDesktopEdgeSurface =>
        _edgeCapsuleHost != null;

    internal IntPtr EnsureVirtualDesktopMainHandle()
    {
        var helper = new WindowInteropHelper(this);
        return helper.Handle != IntPtr.Zero
            ? helper.Handle
            : helper.EnsureHandle();
    }

    internal bool TryMoveToVirtualDesktop(
        VirtualDesktopAdapter adapter,
        Guid desktopId)
    {
        var mainHandle = EnsureVirtualDesktopMainHandle();
        if (mainHandle == IntPtr.Zero)
        {
            return false;
        }

        var mainMoved = adapter.TryMoveWindowToDesktop(
            mainHandle,
            desktopId);
        if (!mainMoved &&
            _windowSwitcherHiddenOwner != IntPtr.Zero)
        {
            mainMoved = adapter.TryMoveWindowToDesktop(
                _windowSwitcherHiddenOwner,
                desktopId) &&
                adapter.TryMoveWindowToDesktop(
                    mainHandle,
                    desktopId);
        }
        if (!mainMoved)
        {
            return false;
        }

        if (_windowSwitcherHiddenOwner != IntPtr.Zero)
        {
            _ = adapter.TryMoveWindowToDesktop(
                _windowSwitcherHiddenOwner,
                desktopId);
        }
        _ = _edgeCapsuleHost?.TryMoveToVirtualDesktop(
            adapter,
            desktopId);
        _ = _experimentalTetherCapsule?.TryMoveToVirtualDesktop(
            adapter,
            desktopId);
        return true;
    }

    internal void MoveVirtualDesktopAuxiliarySurfaces(
        VirtualDesktopAdapter adapter,
        Guid desktopId)
    {
        _ = _edgeCapsuleHost?.TryMoveToVirtualDesktop(
            adapter,
            desktopId);
        _ = _experimentalTetherCapsule?.TryMoveToVirtualDesktop(
            adapter,
            desktopId);
    }
}
