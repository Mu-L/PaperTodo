using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Point = System.Windows.Point;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private ContextMenu BuildDeepCapsuleSlotContextMenu()
    {
        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);

        menu.Opened += (_, _) =>
        {
            if (_deepCapsuleSlotContextMenu != null && !ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu.IsOpen = false;
            }

            System.Threading.Interlocked.Increment(ref _deepCapsuleContextMenuOpenVersion);
            System.Threading.Volatile.Write(ref _deepCapsuleSlotContextMenu, menu);
            SetDeepCapsuleSlotContextMenuOpen(true);
            StartDeepCapsuleContextMenuGuards();
            PromoteDeepCapsuleContextMenu(menu);
            _ = menu.Dispatcher.BeginInvoke(
                () => PromoteDeepCapsuleContextMenu(menu),
                System.Windows.Threading.DispatcherPriority.Input);
        };

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                System.Threading.Volatile.Write(ref _deepCapsuleSlotContextMenu, null);
                SetDeepCapsuleSlotContextMenuOpen(false);
                StopDeepCapsuleContextMenuGuards();
                InvalidateEdgeCapsulePointer();
            }

            // Let WPF finish leaving menu mode before checking the UI thread's native focus state.
            _ = menu.Dispatcher.BeginInvoke(
                ClearStaleDeepCapsuleMenuActivation,
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        };

        return menu;
    }

    private void ClearStaleDeepCapsuleMenuActivation()
    {
        if (_deepCapsuleSlotContextMenu?.IsOpen == true || InputManager.Current.IsInMenuMode)
        {
            return;
        }

        var foreground = WindowNative.ForegroundWindow;
        if (foreground == IntPtr.Zero || IsWindowFromCurrentProcess(foreground))
        {
            return;
        }

        var active = WindowNative.ActiveWindow;
        var focus = WindowNative.KeyboardFocusWindow;
        if ((active == IntPtr.Zero || !IsWindowFromCurrentProcess(active)) &&
            (focus == IntPtr.Zero || !IsWindowFromCurrentProcess(focus)))
        {
            return;
        }

        // A promoted WPF Popup can leave its owner active after closing. Hardcodet's next tray
        // menu then opens and immediately closes, so clear only this stale cross-app handoff.
        Keyboard.ClearFocus();
        WindowNative.ClearCurrentThreadInputActivation(foreground);
    }

    private static void PromoteDeepCapsuleContextMenu(ContextMenu menu)
    {
        if (menu.IsOpen && PresentationSource.FromVisual(menu) is HwndSource source)
        {
            WindowNative.ApplyTopmostZOrder(source.Handle, topmost: true, insertAfter: IntPtr.Zero);
        }
    }

    private void QueueCloseDeepCapsuleSlotContextMenu()
    {
        var menu = System.Threading.Volatile.Read(ref _deepCapsuleSlotContextMenu);
        var version = System.Threading.Interlocked.Read(ref _deepCapsuleContextMenuOpenVersion);
        if (menu == null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => QueueCloseDeepCapsuleSlotContextMenu(menu, version)));
            return;
        }

        QueueCloseDeepCapsuleSlotContextMenu(menu, version);
    }

    private void QueueCloseDeepCapsuleSlotContextMenu(ContextMenu menu, long version)
    {
        if (!ReferenceEquals(_deepCapsuleSlotContextMenu, menu) ||
            _deepCapsuleContextMenuOpenVersion != version ||
            !menu.IsOpen)
        {
            return;
        }

        _pendingDeepCapsuleContextMenuClose = menu;
        _pendingDeepCapsuleContextMenuCloseVersion = version;
        if (_deepCapsuleContextMenuCloseScheduled)
        {
            return;
        }

        _deepCapsuleContextMenuCloseScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(ExecuteQueuedDeepCapsuleContextMenuClose));
    }

    private void ExecuteQueuedDeepCapsuleContextMenuClose()
    {
        var menu = _pendingDeepCapsuleContextMenuClose;
        var version = _pendingDeepCapsuleContextMenuCloseVersion;
        _pendingDeepCapsuleContextMenuClose = null;
        _pendingDeepCapsuleContextMenuCloseVersion = 0;
        _deepCapsuleContextMenuCloseScheduled = false;

        if (menu != null &&
            ReferenceEquals(_deepCapsuleSlotContextMenu, menu) &&
            _deepCapsuleContextMenuOpenVersion == version)
        {
            CloseDeepCapsuleSlotContextMenu();
        }
    }

    private void CloseDeepCapsuleSlotContextMenu()
    {
        var menu = _deepCapsuleSlotContextMenu;
        if (menu?.IsOpen == true)
        {
            menu.IsOpen = false;
        }

        // Closing a WPF ContextMenu normally raises Closed synchronously. Keep this fallback for
        // already-closed/disconnected popups, but never clear a replacement opened re-entrantly.
        if (menu == null || ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
        {
            System.Threading.Volatile.Write(ref _deepCapsuleSlotContextMenu, null);
            SetDeepCapsuleSlotContextMenuOpen(false);
            StopDeepCapsuleContextMenuGuards();
        }
    }

    private void SetDeepCapsuleSlotContextMenuOpen(bool open)
    {
        if (_edgeCapsule.ContextMenuOpen != open)
        {
            SetEdgeCapsuleContextMenuOpen(open);
        }

        // The reducer can reset ContextMenuOpen while detaching. Always synchronize the external
        // owner set as well; HashSet add/remove makes this operation naturally idempotent.
        _controller.SetDeepCapsuleContextMenuOpen(_paper.Id, open);
        RefreshDeepCapsuleSlotTopmost();
    }

    private void StartDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook == IntPtr.Zero)
        {
            _deepCapsuleForegroundHookProc = OnDeepCapsuleForegroundChanged;
            _deepCapsuleForegroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _deepCapsuleForegroundHookProc,
                0,
                0,
                WineventOutOfContext);
        }

        if (_deepCapsuleMouseHook == IntPtr.Zero)
        {
            _deepCapsuleMouseHookProc = OnDeepCapsuleMouseHook;
            _deepCapsuleMouseHook = SetWindowsHookEx(WhMouseLl, _deepCapsuleMouseHookProc, GetModuleHandle(null), 0);
        }
    }

    private void StopDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_deepCapsuleForegroundHook);
            _deepCapsuleForegroundHook = IntPtr.Zero;
        }

        _deepCapsuleForegroundHookProc = null;

        if (_deepCapsuleMouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_deepCapsuleMouseHook);
            _deepCapsuleMouseHook = IntPtr.Zero;
        }

        _deepCapsuleMouseHookProc = null;
    }

    private void OnDeepCapsuleForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || IsWindowFromCurrentProcess(hwnd))
        {
            return;
        }

        QueueCloseDeepCapsuleSlotContextMenu();
    }

    private IntPtr OnDeepCapsuleMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam) && _deepCapsuleSlotContextMenu?.IsOpen == true)
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var screenPoint = new Point(hook.Point.X, hook.Point.Y);
            if (!IsPointInsideDeepCapsuleContextSurface(screenPoint))
            {
                QueueCloseDeepCapsuleSlotContextMenu();
            }
        }

        return CallNextHookEx(_deepCapsuleMouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideDeepCapsuleContextSurface(Point screenPoint)
    {
        if (IsPointInsideElement(_deepCapsuleSlotContextMenu, screenPoint))
        {
            return true;
        }

        return _edgeCapsuleHost?.ContainsWindowScreenPoint(screenPoint) == true;
    }

    private static bool IsPointInsideElement(FrameworkElement? element, Point screenPoint)
    {
        if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var localPoint = element.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= element.ActualWidth &&
                localPoint.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsMouseButtonDownMessage(IntPtr message)
    {
        var value = message.ToInt32();
        return value == WmLButtonDown ||
            value == WmRButtonDown ||
            value == WmMButtonDown ||
            value == WmXButtonDown;
    }

    private static bool IsWindowFromCurrentProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }
}
