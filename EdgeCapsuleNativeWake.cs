using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Installs the native edge-capsule wake bridge after the WPF dispatcher and controller exist.
/// The bridge is runtime behavior, not diagnostics: it only repairs the initial no-owner case
/// where the docked HWND receives real mouse input while the presenter still has a stale
/// PointerOverSurface=false sample.
/// </summary>
internal static class EdgeCapsuleNativeWakeBootstrap
{
    private static Timer? _bootstrapTimer;
    private static int _installQueued;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            _bootstrapTimer = new Timer(
                static _ => TryQueueInstall(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Failure to install the fallback must never affect application startup.
        }
    }

    private static void TryQueueInstall()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null ||
                dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished ||
                Interlocked.Exchange(ref _installQueued, 1) != 0)
            {
                return;
            }

            _ = dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    try
                    {
                        var controller = AppController.Current;
                        if (controller == null)
                        {
                            Interlocked.Exchange(ref _installQueued, 0);
                            return;
                        }

                        controller.AttachEdgeCapsuleNativeWake();
                        _bootstrapTimer?.Dispose();
                        _bootstrapTimer = null;
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _installQueued, 0);
                    }
                }),
                DispatcherPriority.Background);
        }
        catch
        {
            Interlocked.Exchange(ref _installQueued, 0);
        }
    }
}

public sealed partial class AppController
{
    private const int EdgeCapsuleNativeWakeWmMouseMove = 0x0200;
    private const int EdgeCapsuleNativeWakeWmNcMouseMove = 0x00A0;
    private const double EdgeCapsuleNativeWakeRetryMilliseconds = 32;

    private bool _edgeCapsuleNativeWakeAttached;
    private IntPtr _edgeCapsuleNativeWakeLastHwnd;
    private long _edgeCapsuleNativeWakeLastTimestamp;

    internal void AttachEdgeCapsuleNativeWake()
    {
        if (_edgeCapsuleNativeWakeAttached)
        {
            return;
        }

        _edgeCapsuleNativeWakeAttached = true;
        ComponentDispatcher.ThreadPreprocessMessage +=
            OnEdgeCapsuleNativeWakeMessage;
    }

    private void OnEdgeCapsuleNativeWakeMessage(ref MSG msg, ref bool handled)
    {
        if (handled ||
            IsExiting ||
            !State.ExperimentalEdgeCapsuleHoverPreview ||
            _edgeCapsulePreviewSession != null ||
            (msg.message != EdgeCapsuleNativeWakeWmMouseMove &&
             msg.message != EdgeCapsuleNativeWakeWmNcMouseMove) ||
            !WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            return;
        }

        PaperWindow? target = null;
        foreach (var window in _windows.Values)
        {
            if (window.EdgeCapsuleNativeWakeHandle != msg.hwnd ||
                window.IsEdgeCapsulePointerOver ||
                !window.CanEnterEdgeCapsulePreview ||
                !window.IsEdgeCapsuleInteractiveAt(pointer))
            {
                continue;
            }

            target = window;
            break;
        }

        if (target == null)
        {
            return;
        }

        // The native message is already authoritative for occlusion: an obscured underlying HWND
        // does not receive WM_MOUSEMOVE. The geometry check above additionally excludes the fixed
        // transparent host reserve, so this fallback has no authority outside the real capsule.
        var now = Stopwatch.GetTimestamp();
        if (_edgeCapsuleNativeWakeLastHwnd == msg.hwnd &&
            _edgeCapsuleNativeWakeLastTimestamp != 0 &&
            Stopwatch.GetElapsedTime(
                _edgeCapsuleNativeWakeLastTimestamp,
                now).TotalMilliseconds < EdgeCapsuleNativeWakeRetryMilliseconds)
        {
            return;
        }

        _edgeCapsuleNativeWakeLastHwnd = msg.hwnd;
        _edgeCapsuleNativeWakeLastTimestamp = now;
        TraceEdgeCapsulePreview(
            $"native wake target={EdgeCapsulePreviewTraceId(target.EdgeCapsulePreviewPaperId)} " +
            $"pointer={pointer.X},{pointer.Y} hwnd=0x{msg.hwnd.ToInt64():X}");
        target.InvalidateEdgeCapsulePointerFromNativeWake();
    }
}

public sealed partial class PaperWindow
{
    internal IntPtr EdgeCapsuleNativeWakeHandle =>
        _edgeCapsuleHost?.EdgeCapsuleNativeWakeHandle ?? IntPtr.Zero;

    internal void InvalidateEdgeCapsulePointerFromNativeWake() =>
        InvalidateEdgeCapsulePointerFromHostInput();
}

internal sealed partial class EdgeCapsuleHost
{
    internal IntPtr EdgeCapsuleNativeWakeHandle =>
        _disposed
            ? IntPtr.Zero
            : new WindowInteropHelper(Window).Handle;
}
