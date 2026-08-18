using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Reusable layered bitmap source for a 1:1 start cover. Hosts are shown once and remain cloaked;
/// an active lease moves one to the source bounds because an off-screen layered HWND is no longer
/// composed. DirectComposition can still wrap the cloaked window without exposing a second copy.
/// The completed V2.5 path never scales this bitmap.
/// </summary>
internal sealed class EdgeCapsuleProxySnapshotHost : IDisposable
{
    // A normal A-to-B browse briefly owns the outgoing pointer morph and the incoming preview
    // snapshot at the same time. Two warm hosts cover that bounded overlap without returning to
    // the old unbounded/per-queue cache shape.
    private const int MaximumPoolSize = 2;
    private static readonly DeviceScreenRect ParkingBounds =
        new(-32000, -32000, -31996, -31996);
    private static readonly ConditionalWeakTable<
        Dispatcher,
        Stack<EdgeCapsuleProxySnapshotHost>> Pools = new();

    private readonly Dispatcher _dispatcher;
    private readonly Window _window;
    private readonly Image _image;
    private bool _leased;
    private bool _closed;

    private EdgeCapsuleProxySnapshotHost(
        Dispatcher dispatcher,
        Window window,
        Image image)
    {
        _dispatcher = dispatcher;
        _window = window;
        _image = image;
    }

    public IntPtr Handle => _closed
        ? IntPtr.Zero
        : new WindowInteropHelper(_window).Handle;

    internal static void Prewarm(
        Dispatcher dispatcher,
        int count = MaximumPoolSize)
    {
        if (dispatcher.HasShutdownStarted || count <= 0)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => Prewarm(
                    dispatcher,
                    count)));
            return;
        }

        var pool = Pools.GetValue(
            dispatcher,
            static _ =>
                new Stack<EdgeCapsuleProxySnapshotHost>());
        while (pool.Count <
               Math.Min(MaximumPoolSize, count))
        {
            var host = TryCreateHost(dispatcher);
            if (host == null)
            {
                break;
            }
            pool.Push(host);
        }
    }

    public static EdgeCapsuleProxySnapshotHost? TryCreate(
        BitmapSource bitmap,
        EdgeCapsulePresentationFrame source)
    {
        var host = TryAcquire(bitmap, source);
        if (host == null ||
            !host.TryPrepareLayout(bitmap, source))
        {
            host?.ClosePermanently();
            return null;
        }

        host._leased = true;
        try
        {
            host._dispatcher.Invoke(
                static () => { },
                DispatcherPriority.Render);
            return host;
        }
        catch
        {
            host.ClosePermanently();
            return null;
        }
    }

    /// <summary>
    /// Prepares a candidate snapshot without nesting a Render-priority dispatcher frame. The
    /// caller can overlap this publication turn with the hover-residence gate, then hand the warm
    /// lease to the visual transaction without blocking its Send-priority commit.
    /// </summary>
    public static async Task<EdgeCapsuleProxySnapshotHost?> TryCreateAsync(
        BitmapSource bitmap,
        EdgeCapsulePresentationFrame source)
    {
        var host = TryAcquire(bitmap, source);
        if (host == null ||
            !host.TryPrepareLayout(bitmap, source))
        {
            host?.ClosePermanently();
            return null;
        }

        host._leased = true;
        try
        {
            await host._dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render);
            if (host._closed ||
                host._dispatcher.HasShutdownStarted)
            {
                host.ClosePermanently();
                return null;
            }
            return host;
        }
        catch
        {
            host.ClosePermanently();
            return null;
        }
    }

    private static EdgeCapsuleProxySnapshotHost? TryAcquire(
        BitmapSource bitmap,
        EdgeCapsulePresentationFrame source)
    {
        if (source.Bounds.IsEmpty ||
            bitmap.PixelWidth <= 0 ||
            bitmap.PixelHeight <= 0)
        {
            return null;
        }

        var dispatcher =
            Application.Current?.Dispatcher ??
            Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess() ||
            dispatcher.HasShutdownStarted)
        {
            return null;
        }

        var pool = Pools.GetValue(
            dispatcher,
            static _ =>
                new Stack<EdgeCapsuleProxySnapshotHost>());
        EdgeCapsuleProxySnapshotHost? host = null;
        while (pool.Count > 0 && host == null)
        {
            var candidate = pool.Pop();
            if (!candidate._closed)
            {
                host = candidate;
            }
        }

        return host ?? TryCreateHost(dispatcher);
    }

    private static EdgeCapsuleProxySnapshotHost? TryCreateHost(
        Dispatcher dispatcher)
    {
        Window? window = null;
        try
        {
            var image = new Image
            {
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            window = new Window
            {
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = false,
                Left = -32000,
                Top = -32000,
                Width = 4,
                Height = 4,
                Content = image,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var handle =
                new WindowInteropHelper(window).EnsureHandle();
            if (handle == IntPtr.Zero)
            {
                window.Close();
                return null;
            }

            WindowNative.ApplyNoActivateStyle(window);
            WindowNative.SetInputPassthrough(
                window,
                enabled: true);
            window.Show();
            WindowNative.ApplyBottomZOrder(window);
            _ = WindowNative.TrySetWindowDeviceBounds(
                window,
                ParkingBounds);
            if (!WindowNative.TrySetWindowCloaked(
                    handle,
                    cloaked: true))
            {
                window.Close();
                return null;
            }
            return new EdgeCapsuleProxySnapshotHost(
                dispatcher,
                window,
                image);
        }
        catch
        {
            try { window?.Close(); } catch { }
            return null;
        }
    }

    private bool TryPrepareLayout(
        BitmapSource bitmap,
        EdgeCapsulePresentationFrame source)
    {
        if (_closed ||
            _leased ||
            _dispatcher.HasShutdownStarted)
        {
            return false;
        }

        try
        {
            var handle = Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            _image.Source = bitmap;
            _window.Width =
                source.Bounds.Width /
                Math.Max(1, source.DpiScaleX);
            _window.Height =
                source.Bounds.Height /
                Math.Max(1, source.DpiScaleY);
            if (!WindowNative.TrySetWindowDeviceBounds(
                    _window,
                    source.Bounds))
            {
                return false;
            }

            _window.UpdateLayout();
            WindowNative.ApplyBottomZOrder(_window);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_closed || !_leased)
        {
            return;
        }

        _leased = false;
        try
        {
            _ = WindowNative.TrySetWindowCloaked(
                Handle,
                cloaked: true);
        }
        catch { }
        try
        {
            _ = WindowNative.TrySetWindowDeviceBounds(
                _window,
                ParkingBounds);
        }
        catch { }
        _image.Source = null;

        if (_dispatcher.HasShutdownStarted)
        {
            ClosePermanently();
            return;
        }

        var pool = Pools.GetValue(
            _dispatcher,
            static _ =>
                new Stack<EdgeCapsuleProxySnapshotHost>());
        if (pool.Count >= MaximumPoolSize)
        {
            ClosePermanently();
            return;
        }
        pool.Push(this);
    }

    private void ClosePermanently()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _leased = false;
        _image.Source = null;
        try { _window.Close(); } catch { }
    }
}
