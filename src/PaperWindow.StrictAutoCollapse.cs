using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const int StrictAutoCollapseArmIntervalMilliseconds = 50;
    private const int StrictAutoCollapsePollIntervalMilliseconds = 200;

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
    private readonly List<int> _strictAutoCollapseOpeningKeys = [];
    private int _strictAutoCollapseGeneration;
    private bool _strictAutoCollapsePending;
    private bool _strictAutoCollapseReady;
    private bool _strictAutoCollapseWasForeground;
    private int _strictAutoCollapseSettledTicks;
    private uint _strictAutoCollapseLastInputTime;
    private StrictNativePoint _strictAutoCollapseCursor;

    private void InitializeStrictAutoCollapseTracking()
    {
        PreviewMouseDown += (_, _) => MarkStrictPaperUsed();
        PreviewMouseWheel += (_, _) => MarkStrictPaperUsed();
        PreviewKeyDown += (_, _) => MarkStrictPaperUsed();
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
        _strictAutoCollapseSettledTicks = 0;
        _strictAutoCollapseOpeningKeys.Clear();
        if (!_strictAutoCollapsePending)
        {
            return;
        }

        CaptureStrictAutoCollapseOpeningKeys();

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
                    Interval = TimeSpan.FromMilliseconds(
                        StrictAutoCollapseArmIntervalMilliseconds)
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
        _strictAutoCollapseSettledTicks = 0;
        _strictAutoCollapseOpeningKeys.Clear();
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
        var lastInputTime = ReadStrictLastInputTime();
        if (!_strictAutoCollapseReady)
        {
            // Key-up events from the shortcut that showed the paper are part of that opening
            // gesture, not the later operation that should collapse an unused paper.
            if (HasHeldStrictAutoCollapseOpeningKey())
            {
                _strictAutoCollapseLastInputTime = lastInputTime;
                _strictAutoCollapseSettledTicks = 0;
                GetCursorPos(out _strictAutoCollapseCursor);
                _strictAutoCollapseWasForeground = ownsForeground;
                return;
            }

            // Wait for a short input-idle boundary after the opening keys have been released.
            if (_strictAutoCollapseLastInputTime != lastInputTime)
            {
                _strictAutoCollapseLastInputTime = lastInputTime;
                _strictAutoCollapseSettledTicks = 0;
                GetCursorPos(out _strictAutoCollapseCursor);
                _strictAutoCollapseWasForeground = ownsForeground;
                return;
            }

            if (++_strictAutoCollapseSettledTicks < 2)
            {
                return;
            }

            _strictAutoCollapseLastInputTime = lastInputTime;
            GetCursorPos(out _strictAutoCollapseCursor);
            _strictAutoCollapseWasForeground = ownsForeground;
            _strictAutoCollapseReady = true;
            if (_strictAutoCollapseTimer != null)
            {
                // Keep the short arming cadence only long enough to let the show gesture
                // settle, then reduce steady-state polling while the unused paper is pending.
                _strictAutoCollapseTimer.Interval = TimeSpan.FromMilliseconds(
                    StrictAutoCollapsePollIntervalMilliseconds);
            }
            return;
        }

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
        var pointerAction = HasStrictPointerButtonActivity();

        // Real input delivered to the paper cancels pending state through the Preview* handlers
        // before this poll runs. If the paper still owns foreground here, cursor motion alone is
        // harmless; keyboard/global-hotkey input or a pointer click not delivered to the paper is
        // another operation and should fold the still-unused paper.
        if (ownsForeground)
        {
            if (!cursorMoved || pointerAction)
            {
                CollapseStrictPendingPaper();
            }
            _strictAutoCollapseWasForeground = true;
            return;
        }

        // Foreground leaving this paper is already a strong signal. For papers shown without
        // activation, ignore pure cursor motion but react to keyboard/wheel input or pointer clicks.
        if (_strictAutoCollapseWasForeground || !cursorMoved || pointerAction)
        {
            CollapseStrictPendingPaper();
            return;
        }

        _strictAutoCollapseWasForeground = false;
    }

    private void CollapseStrictPendingPaper()
    {
        CancelStrictAutoCollapse();
        if (CanDisplayAsCapsule() && !HasExperimentalAutoCollapseBlocker())
        {
            SetCollapsedState(true);
        }
    }

    private static uint ReadStrictLastInputTime()
    {
        var info = new StrictLastInputInfo
        {
            Size = (uint)Marshal.SizeOf<StrictLastInputInfo>()
        };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    private void CaptureStrictAutoCollapseOpeningKeys()
    {
        const int firstKeyboardVirtualKey = 0x08;
        const int lastKeyboardVirtualKey = 0xFE;

        for (var key = firstKeyboardVirtualKey; key <= lastKeyboardVirtualKey; key++)
        {
            if (IsStrictKeyDown(key))
            {
                _strictAutoCollapseOpeningKeys.Add(key);
            }
        }
    }

    private bool HasHeldStrictAutoCollapseOpeningKey()
    {
        var anyHeld = false;
        for (var i = _strictAutoCollapseOpeningKeys.Count - 1; i >= 0; i--)
        {
            if (IsStrictKeyDown(_strictAutoCollapseOpeningKeys[i]))
            {
                anyHeld = true;
            }
            else
            {
                _strictAutoCollapseOpeningKeys.RemoveAt(i);
            }
        }

        return anyHeld;
    }

    private static bool IsStrictKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool HasStrictPointerButtonActivity()
    {
        const int virtualKeyLeftButton = 0x01;
        const int virtualKeyRightButton = 0x02;
        const int virtualKeyMiddleButton = 0x04;
        const int virtualKeyXButton1 = 0x05;
        const int virtualKeyXButton2 = 0x06;

        static bool Active(int key)
        {
            var state = GetAsyncKeyState(key);
            return (state & 0x8000) != 0 || (state & 0x0001) != 0;
        }

        return Active(virtualKeyLeftButton) ||
            Active(virtualKeyRightButton) ||
            Active(virtualKeyMiddleButton) ||
            Active(virtualKeyXButton1) ||
            Active(virtualKeyXButton2);
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
