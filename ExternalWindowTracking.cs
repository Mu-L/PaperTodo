using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace PaperTodo;

[Flags]
internal enum ExternalWindowEventKind
{
    None = 0,
    Foreground = 1,
    Location = 2,
    MinimizeStarted = 4,
    MinimizeEnded = 8,
    Destroyed = 16,
    Cloaked = 32,
    Uncloaked = 64,
    DesktopSwitched = 128,
    MoveSizeStarted = 256,
    MoveSizeEnded = 512
}

internal readonly record struct ExternalWindowIdentity(
    IntPtr Handle,
    uint ProcessId)
{
    public bool IsEmpty => Handle == IntPtr.Zero || ProcessId == 0;
}

internal readonly record struct ExternalWindowSnapshot(
    ExternalWindowIdentity Identity,
    string Title,
    DeviceScreenRect Bounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    double DpiScale)
{
    public bool IsUsableTarget =>
        !Identity.IsEmpty &&
        IsVisible &&
        !IsMinimized &&
        !IsCloaked &&
        !Bounds.IsEmpty;
}

internal readonly record struct ExternalWindowEvent(
    IntPtr Handle,
    ExternalWindowEventKind Kind);

internal sealed class ExternalWindowTracker : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly Dispatcher _dispatcher;
    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = new();
    private readonly Dictionary<IntPtr, ExternalWindowEventKind> _pending = new();
    private DispatcherOperation? _pendingFlush;
    private int _generation;
    private bool _disposed;

    public ExternalWindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = HandleNativeEvent;

        AddHook(EventSystemForeground, EventSystemForeground);
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventSystemMinimizeStart, EventSystemMinimizeEnd);
        AddHook(EventSystemDesktopSwitch, EventSystemDesktopSwitch);
        AddHook(EventObjectDestroy, EventObjectDestroy);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
        AddHook(EventObjectCloaked, EventObjectUncloaked);
    }

    public event Action<ExternalWindowEvent>? Changed;

    public bool IsActive => !_disposed && _hooks.Count > 0;

    private void AddHook(uint eventMin, uint eventMax)
    {
        var hook = SetWinEventHook(
            eventMin,
            eventMax,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void HandleNativeEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed)
        {
            return;
        }

        var kind = EventKind(eventType);
        if (kind == ExternalWindowEventKind.None)
        {
            return;
        }

        if (eventType >= EventObjectDestroy &&
            (idObject != ObjIdWindow || idChild != ChildIdSelf))
        {
            return;
        }

        Queue(hwnd, kind);
    }

    private static ExternalWindowEventKind EventKind(uint eventType)
    {
        return eventType switch
        {
            EventSystemForeground => ExternalWindowEventKind.Foreground,
            EventSystemMoveSizeStart =>
                ExternalWindowEventKind.Location |
                ExternalWindowEventKind.MoveSizeStarted,
            EventSystemMoveSizeEnd =>
                ExternalWindowEventKind.Location |
                ExternalWindowEventKind.MoveSizeEnded,
            EventSystemMinimizeStart => ExternalWindowEventKind.MinimizeStarted,
            EventSystemMinimizeEnd => ExternalWindowEventKind.MinimizeEnded,
            EventSystemDesktopSwitch => ExternalWindowEventKind.DesktopSwitched,
            EventObjectDestroy => ExternalWindowEventKind.Destroyed,
            EventObjectLocationChange => ExternalWindowEventKind.Location,
            EventObjectCloaked => ExternalWindowEventKind.Cloaked,
            EventObjectUncloaked => ExternalWindowEventKind.Uncloaked,
            _ => ExternalWindowEventKind.None
        };
    }

    private void Queue(IntPtr hwnd, ExternalWindowEventKind kind)
    {
        var generation = _generation;
        if (_dispatcher.CheckAccess())
        {
            QueueOnDispatcher(hwnd, kind, generation);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            (Action)(() => QueueOnDispatcher(hwnd, kind, generation)),
            DispatcherPriority.Render);
    }

    private void QueueOnDispatcher(
        IntPtr hwnd,
        ExternalWindowEventKind kind,
        int generation)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        _pending[hwnd] = _pending.GetValueOrDefault(hwnd) | kind;
        if (_pendingFlush is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingFlush = _dispatcher.BeginInvoke(
            (Action)(() => Flush(generation)),
            DispatcherPriority.Render);
    }

    private void Flush(int generation)
    {
        _pendingFlush = null;
        if (_disposed || generation != _generation)
        {
            _pending.Clear();
            return;
        }

        var events = _pending
            .Select(pair => new ExternalWindowEvent(pair.Key, pair.Value))
            .ToArray();
        _pending.Clear();
        foreach (var item in events)
        {
            Changed?.Invoke(item);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        if (_pendingFlush is { Status: DispatcherOperationStatus.Pending } operation)
        {
            operation.Abort();
        }
        _pendingFlush = null;
        _pending.Clear();
        Changed = null;

        foreach (var hook in _hooks)
        {
            _ = UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookAssembly,
        WinEventProc eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);
}

internal static class ExternalWindowNative
{
    private const uint GaRoot = 2;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int SwRestore = 9;
    private const int MinimumTargetWidth = 80;
    private const int MinimumTargetHeight = 40;

    public static IReadOnlyList<ExternalWindowSnapshot> EnumerateTargets(
        int maximumCount = 24)
    {
        var results = new List<ExternalWindowSnapshot>();
        var occluders = new List<DeviceScreenRect>();
        var visibleAreas = WindowWorkAreaHelper
            .ConnectedMonitorGeometries()
            .Select(monitor => monitor.WorkArea)
            .Where(area => !area.IsEmpty)
            .ToArray();
        var shellWindow = GetShellWindow();
        _ = EnumWindows((hwnd, _) =>
        {
            if (results.Count >= Math.Max(1, maximumCount))
            {
                return false;
            }

            if (hwnd == shellWindow ||
                !TryGetSnapshot(hwnd, out var snapshot) ||
                !snapshot.IsUsableTarget)
            {
                return true;
            }

            if (IsSelectableTarget(hwnd, snapshot) &&
                HasExposedArea(
                    snapshot.Bounds,
                    visibleAreas,
                    occluders))
            {
                results.Add(snapshot);
            }

            // EnumWindows is ordered from top to bottom. Every usable external
            // window already visited therefore hides the rectangular area it
            // covers from candidates below it.
            occluders.Add(snapshot.Bounds);
            return true;
        }, IntPtr.Zero);
        return results;
    }

    public static bool TryGetTargetAtPoint(
        DeviceScreenPoint point,
        out ExternalWindowSnapshot snapshot)
    {
        ExternalWindowSnapshot? match = null;
        var x = (int)Math.Round(point.X, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round(point.Y, MidpointRounding.AwayFromZero);
        var shellWindow = GetShellWindow();
        _ = EnumWindows((hwnd, _) =>
        {
            if (hwnd == shellWindow ||
                !TryGetSnapshot(hwnd, out var candidate) ||
                !candidate.IsUsableTarget ||
                x < candidate.Bounds.Left ||
                x >= candidate.Bounds.Right ||
                y < candidate.Bounds.Top ||
                y >= candidate.Bounds.Bottom)
            {
                return true;
            }

            // Do not bind through a visible tool/dialog/overlay to a selectable
            // window hidden below it.
            if (IsSelectableTarget(hwnd, candidate))
            {
                match = candidate;
            }
            return false;
        }, IntPtr.Zero);

        snapshot = match.GetValueOrDefault();
        return match.HasValue;
    }

    public static bool TryGetSnapshot(
        ExternalWindowIdentity identity,
        out ExternalWindowSnapshot snapshot)
    {
        snapshot = default;
        return !identity.IsEmpty &&
            TryGetSnapshot(identity.Handle, out snapshot) &&
            snapshot.Identity.ProcessId == identity.ProcessId;
    }

    public static bool TryGetSnapshot(
        IntPtr hwnd,
        out ExternalWindowSnapshot snapshot)
    {
        snapshot = default;
        if (hwnd == IntPtr.Zero ||
            !IsWindow(hwnd) ||
            TryGetProcessId(hwnd, out var processId) == false ||
            processId == 0 ||
            processId == Environment.ProcessId)
        {
            return false;
        }

        var root = GetAncestor(hwnd, GaRoot);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
            if (!TryGetProcessId(hwnd, out processId) ||
                processId == 0 ||
                processId == Environment.ProcessId)
            {
                return false;
            }
        }

        var visible = IsWindowVisible(hwnd);
        var minimized = IsIconic(hwnd);
        var cloaked = IsCloaked(hwnd);
        _ = TryGetBounds(hwnd, out var bounds);
        snapshot = new ExternalWindowSnapshot(
            new ExternalWindowIdentity(hwnd, processId),
            WindowTitle(hwnd),
            bounds,
            visible,
            minimized,
            cloaked,
            DpiScaleFor(hwnd));
        return true;
    }

    public static bool IsSameProcess(
        ExternalWindowIdentity identity,
        IntPtr candidate)
    {
        return !identity.IsEmpty &&
            candidate != IntPtr.Zero &&
            TryGetProcessId(candidate, out var processId) &&
            processId == identity.ProcessId;
    }

    public static bool IsIdentityValid(ExternalWindowIdentity identity)
    {
        return TryGetProcessId(identity.Handle, out var processId) &&
            processId == identity.ProcessId;
    }

    public static bool TryGetProcessId(IntPtr hwnd, out uint processId)
    {
        processId = 0;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(hwnd, out processId);
        return processId != 0;
    }

    public static bool TryGetBounds(IntPtr hwnd, out DeviceScreenRect bounds)
    {
        if (hwnd != IntPtr.Zero &&
            DwmGetWindowAttribute(
                hwnd,
                DwmwaExtendedFrameBounds,
                out NativeRect frame,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            bounds = frame.ToDeviceRect();
            if (!bounds.IsEmpty)
            {
                return true;
            }
        }

        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            bounds = rect.ToDeviceRect();
            return !bounds.IsEmpty;
        }

        bounds = default;
        return false;
    }

    public static IntPtr ForegroundWindow => GetForegroundWindow();

    public static bool RestoreAndActivate(ExternalWindowIdentity identity)
    {
        if (!IsIdentityValid(identity))
        {
            return false;
        }

        if (IsIconic(identity.Handle))
        {
            _ = ShowWindow(identity.Handle, SwRestore);
        }

        return SetForegroundWindow(identity.Handle);
    }

    private static bool IsToolWindow(IntPtr hwnd) =>
        (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0;

    private static bool IsSelectableTarget(
        IntPtr hwnd,
        ExternalWindowSnapshot snapshot)
    {
        return snapshot.IsUsableTarget &&
            !IsToolWindow(hwnd) &&
            snapshot.Title.Length > 0 &&
            snapshot.Bounds.Width >= MinimumTargetWidth &&
            snapshot.Bounds.Height >= MinimumTargetHeight;
    }

    private static bool HasExposedArea(
        DeviceScreenRect target,
        IReadOnlyList<DeviceScreenRect> visibleAreas,
        IReadOnlyList<DeviceScreenRect> occluders)
    {
        var exposed = new List<DeviceScreenRect>();
        if (visibleAreas.Count == 0)
        {
            exposed.Add(target);
        }
        else
        {
            foreach (var visibleArea in visibleAreas)
            {
                if (TryIntersect(
                        target,
                        visibleArea,
                        out var onScreen))
                {
                    exposed.Add(onScreen);
                }
            }
        }

        if (exposed.Count == 0)
        {
            return false;
        }

        foreach (var occluder in occluders)
        {
            if (!TryIntersect(target, occluder, out _))
            {
                continue;
            }

            var remainder = new List<DeviceScreenRect>();
            foreach (var area in exposed)
            {
                Subtract(area, occluder, remainder);
            }

            if (remainder.Count == 0)
            {
                return false;
            }
            exposed = remainder;
        }

        return exposed.Count > 0;
    }

    private static void Subtract(
        DeviceScreenRect source,
        DeviceScreenRect cover,
        ICollection<DeviceScreenRect> remainder)
    {
        if (!TryIntersect(source, cover, out var overlap))
        {
            remainder.Add(source);
            return;
        }

        AddIfVisible(remainder, new DeviceScreenRect(
            source.Left,
            source.Top,
            source.Right,
            overlap.Top));
        AddIfVisible(remainder, new DeviceScreenRect(
            source.Left,
            overlap.Bottom,
            source.Right,
            source.Bottom));
        AddIfVisible(remainder, new DeviceScreenRect(
            source.Left,
            overlap.Top,
            overlap.Left,
            overlap.Bottom));
        AddIfVisible(remainder, new DeviceScreenRect(
            overlap.Right,
            overlap.Top,
            source.Right,
            overlap.Bottom));
    }

    private static bool TryIntersect(
        DeviceScreenRect first,
        DeviceScreenRect second,
        out DeviceScreenRect intersection)
    {
        intersection = new DeviceScreenRect(
            Math.Max(first.Left, second.Left),
            Math.Max(first.Top, second.Top),
            Math.Min(first.Right, second.Right),
            Math.Min(first.Bottom, second.Bottom));
        return !intersection.IsEmpty;
    }

    private static void AddIfVisible(
        ICollection<DeviceScreenRect> areas,
        DeviceScreenRect area)
    {
        if (!area.IsEmpty)
        {
            areas.Add(area);
        }
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(
            hwnd,
            DwmwaCloaked,
            out int cloaked,
            sizeof(int)) == 0 &&
        cloaked != 0;

    private static double DpiScaleFor(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(length + 1, 512));
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly DeviceScreenRect ToDeviceRect() =>
            new(Left, Top, Right, Bottom);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsProc enumFunction,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hwnd,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hwnd,
        StringBuilder text,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(
        IntPtr hwnd,
        out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out int value,
        int valueSize);
}
