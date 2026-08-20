using System.Runtime.InteropServices;

namespace PaperTodo;

internal readonly record struct VirtualDesktopProbeResult(
    bool ManagerAvailable,
    bool CurrentDesktopResolved,
    int HResult)
{
    public bool IsUsable =>
        ManagerAvailable &&
        CurrentDesktopResolved &&
        HResult >= 0;
}

// Uses only the documented Windows 10+ IVirtualDesktopManager surface. No internal shell
// interfaces or build-specific vtable layouts are involved.
internal sealed class VirtualDesktopAdapter : IDisposable
{
    private static readonly Guid VirtualDesktopManagerClassId =
        new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private const int EFail = unchecked((int)0x80004005);
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsPopup = unchecked((int)0x80000000);

    private IVirtualDesktopManager? _manager;
    private bool _activationAttempted;
    private bool _disposed;

    public int LastHResult { get; private set; }

    public VirtualDesktopProbeResult Probe()
    {
        if (!TryGetManager(out _))
        {
            return new VirtualDesktopProbeResult(
                ManagerAvailable: false,
                CurrentDesktopResolved: false,
                LastHResult);
        }

        var resolved = TryGetCurrentDesktopId(out _);
        return new VirtualDesktopProbeResult(
            ManagerAvailable: true,
            CurrentDesktopResolved: resolved,
            LastHResult);
    }

    public bool TryIsWindowOnCurrentDesktop(
        IntPtr window,
        out bool onCurrentDesktop)
    {
        onCurrentDesktop = false;
        if (window == IntPtr.Zero ||
            !TryGetManager(out var manager))
        {
            LastHResult = window == IntPtr.Zero
                ? EInvalidArg
                : LastHResult;
            return false;
        }

        try
        {
            var result = manager.IsWindowOnCurrentVirtualDesktop(
                window,
                out var onCurrent);
            LastHResult = result;
            if (result < 0)
            {
                return false;
            }

            onCurrentDesktop = onCurrent != 0;
            return true;
        }
        catch (Exception ex)
        {
            LastHResult = Marshal.GetHRForException(ex);
            return false;
        }
    }

    public bool TryGetWindowDesktopId(
        IntPtr window,
        out Guid desktopId)
    {
        desktopId = Guid.Empty;
        if (window == IntPtr.Zero ||
            !TryGetManager(out var manager))
        {
            LastHResult = window == IntPtr.Zero
                ? EInvalidArg
                : LastHResult;
            return false;
        }

        try
        {
            var result = manager.GetWindowDesktopId(
                window,
                out desktopId);
            LastHResult = result;
            return result >= 0 && desktopId != Guid.Empty;
        }
        catch (Exception ex)
        {
            LastHResult = Marshal.GetHRForException(ex);
            desktopId = Guid.Empty;
            return false;
        }
    }

    public bool TryMoveWindowToDesktop(
        IntPtr window,
        Guid desktopId)
    {
        if (window == IntPtr.Zero ||
            desktopId == Guid.Empty ||
            !TryGetManager(out var manager))
        {
            LastHResult =
                window == IntPtr.Zero || desktopId == Guid.Empty
                    ? EInvalidArg
                    : LastHResult;
            return false;
        }

        try
        {
            var result = manager.MoveWindowToDesktop(
                window,
                ref desktopId);
            LastHResult = result;
            return result >= 0;
        }
        catch (Exception ex)
        {
            LastHResult = Marshal.GetHRForException(ex);
            return false;
        }
    }

    public bool TryGetCurrentDesktopId(out Guid desktopId)
    {
        desktopId = Guid.Empty;
        if (!TryGetManager(out _))
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero &&
            TryIsWindowOnCurrentDesktop(
                foreground,
                out var foregroundIsCurrent) &&
            foregroundIsCurrent &&
            TryGetWindowDesktopId(foreground, out desktopId))
        {
            return true;
        }

        var referenceWindow = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            "Static",
            "",
            WsPopup,
            -32000,
            -32000,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (referenceWindow == IntPtr.Zero)
        {
            LastHResult = HResultFromWin32(
                Marshal.GetLastPInvokeError());
            return false;
        }

        try
        {
            return TryGetWindowDesktopId(
                referenceWindow,
                out desktopId);
        }
        finally
        {
            _ = DestroyWindow(referenceWindow);
        }
    }

    private bool TryGetManager(
        out IVirtualDesktopManager manager)
    {
        manager = null!;
        if (_disposed)
        {
            LastHResult = EFail;
            return false;
        }

        if (_manager != null)
        {
            manager = _manager;
            return true;
        }

        if (_activationAttempted)
        {
            return false;
        }

        _activationAttempted = true;
        try
        {
            var managerType = Type.GetTypeFromCLSID(
                VirtualDesktopManagerClassId,
                throwOnError: false);
            if (managerType == null ||
                Activator.CreateInstance(managerType) is not
                    IVirtualDesktopManager created)
            {
                LastHResult = EFail;
                return false;
            }

            _manager = created;
            manager = created;
            LastHResult = 0;
            return true;
        }
        catch (Exception ex)
        {
            LastHResult = Marshal.GetHRForException(ex);
            return false;
        }
    }

    private static int HResultFromWin32(int error)
    {
        return error <= 0
            ? EFail
            : unchecked((int)(
                0x80070000u |
                ((uint)error & 0x0000FFFFu)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var manager = _manager;
        _manager = null;
        if (manager != null && Marshal.IsComObject(manager))
        {
            try
            {
                _ = Marshal.FinalReleaseComObject(manager);
            }
            catch
            {
                // COM teardown is best effort during feature disable and app exit.
            }
        }
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(
            IntPtr topLevelWindow,
            out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(
            IntPtr topLevelWindow,
            out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(
            IntPtr topLevelWindow,
            ref Guid desktopId);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
}
