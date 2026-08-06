using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StrictLastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StrictNativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref StrictLastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out StrictNativePoint point);

    private DispatcherTimer? _strictAutoCollapseTimer;
    private int _strictAutoCollapseGeneration;
    private bool _strictAutoCollapsePending;
    private bool _strictAutoCollapseReady;
    private bool _strictAutoCollapseWasForeground;
    private uint _strictAutoCollapseLastInputTime;
    private StrictNativePoint _strictAutoCollapseCursor;

    private void InitializeStrictAutoCollapseTracking()
    {
        PreviewMouseDown += (_, _) => MarkStrictPaperUsed();
        PreviewKeyDown += (_, e) =>
        {
            if (!IsStrictExternalShortcutDown(e.Key))
            {
                MarkStrictPaperUsed();
            }
        };
        PreviewStylusDown += (_, _) => MarkStrictPaperUsed();
        PreviewTouchDown += (_, _) => MarkStrictPaperUsed();
    }

    internal void ArmStrictAutoCollapseAfterShow()
    {
        var generation = ++_strictAutoCollapseGeneration;
        StopStrictAutoCollapseTimer();
        _strictAutoCollapsePending =
            _controller.State.ExperimentalCollapsePaperOnDeactivate &&
            _controller.State.ExperimentalStrictCollapsePaperAfterShow &&
            _controller.State.UseCapsuleMode &&
            !_paper.IsCollapsed &&
            _paper.IsVisible;
        _strictAutoCollapseReady = false;
        if (!_strictAutoCollapsePending)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _strictAutoCollapseGeneration ||
                    !_strictAutoCollapsePending)
                {
                    return;
                }

                _strictAutoCollapseTimer = new DispatcherTimer(
                    DispatcherPriority.Background,
                    Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _strictAutoCollapseTimer.Tick += OnStrictAutoCollapseTick;
                _strictAutoCollapseTimer.Start();
            }),
            DispatcherPriority.ContextIdle);
    }

    internal void CancelStrictAutoCollapse()
    {
        _strictAutoCollapseGeneration++;
        _strictAutoCollapsePending = false;
        _strictAutoCollapseReady = false;
        StopStrictAutoCollapseTimer();
    }

    private void MarkStrictPaperUsed()
    {
        if (_strictAutoCollapsePending)
        {
            CancelStrictAutoCollapse();
        }
    }

    private void OnStrictAutoCollapseTick(object? sender, EventArgs e)
    {
        if (!_strictAutoCollapsePending ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            !_controller.State.ExperimentalCollapsePaperOnDeactivate ||
            !_controller.State.ExperimentalStrictCollapsePaperAfterShow ||
            !_controller.State.UseCapsuleMode ||
            !_paper.IsVisible ||
            _paper.IsCollapsed ||
            !IsVisible ||
            WindowState == WindowState.Minimized)
        {
            CancelStrictAutoCollapse();
            return;
        }

        var foreground = GetForegroundWindow();
        var ownsForeground = OwnsNativeWindow(foreground);
        if (!_strictAutoCollapseReady)
        {
            // The shortcut that created or showed the paper can still have keys held down.
            // Arm only after all buttons are released so its key-up cannot count as a later action.
            if (HasStrictPhysicalInputDown())
            {
                return;
            }

            _strictAutoCollapseLastInputTime = ReadStrictLastInputTime();
            GetCursorPos(out _strictAutoCollapseCursor);
            _strictAutoCollapseWasForeground = ownsForeground;
            _strictAutoCollapseReady = true;
            return;
        }

        var lastInputTime = ReadStrictLastInputTime();
        if (lastInputTime == _strictAutoCollapseLastInputTime)
        {
            _strictAutoCollapseWasForeground = ownsForeground;
            return;
        }

        _strictAutoCollapseLastInputTime = lastInputTime;
        GetCursorPos(out var cursor);
        var cursorMoved =
            cursor.X != _strictAutoCollapseCursor.X ||
            cursor.Y != _strictAutoCollapseCursor.Y;
        _strictAutoCollapseCursor = cursor;
        var inputDown = HasStrictPhysicalInputDown();

        if (ownsForeground)
        {
            if (!IsStrictExternalShortcutDown(Key.None) &&
                (inputDown || !cursorMoved))
            {
                MarkStrictPaperUsed();
            }
            _strictAutoCollapseWasForeground = true;
            return;
        }

        // Foreground leaving this paper is an explicit other operation. For papers shown without
        // activation, require a key/button press; cursor movement alone must not fold the paper.
        if (_strictAutoCollapseWasForeground || inputDown || !cursorMoved)
        {
            CancelStrictAutoCollapse();
            if (CanDisplayAsCapsule() && !HasExperimentalAutoCollapseBlocker())
            {
                SetCollapsedState(true);
            }
            return;
        }

        _strictAutoCollapseWasForeground = false;
    }

    private static uint ReadStrictLastInputTime()
    {
        var info = new StrictLastInputInfo
        {
            Size = (uint)Marshal.SizeOf<StrictLastInputInfo>()
        };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    private static bool IsStrictExternalShortcutDown(Key eventKey)
    {
        const int virtualKeyTab = 0x09;
        const int virtualKeyMenu = 0x12;
        const int virtualKeyControl = 0x11;
        const int virtualKeyShift = 0x10;
        const int virtualKeyLeftWindows = 0x5B;
        const int virtualKeyRightWindows = 0x5C;

        static bool Down(int key) =>
            (GetAsyncKeyState(key) & 0x8000) != 0;

        if (Down(virtualKeyLeftWindows) || Down(virtualKeyRightWindows))
        {
            return true;
        }
        if (Down(virtualKeyMenu) &&
            (Down(virtualKeyTab) || eventKey == Key.Tab))
        {
            return true;
        }
        return Down(virtualKeyControl) &&
            Down(virtualKeyMenu) &&
            Down(virtualKeyShift);
    }

    private static bool HasStrictPhysicalInputDown()
    {
        for (var virtualKey = 1; virtualKey < 255; virtualKey++)
        {
            if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private void StopStrictAutoCollapseTimer()
    {
        if (_strictAutoCollapseTimer == null)
        {
            return;
        }

        _strictAutoCollapseTimer.Stop();
        _strictAutoCollapseTimer.Tick -= OnStrictAutoCollapseTick;
        _strictAutoCollapseTimer = null;
    }
}
