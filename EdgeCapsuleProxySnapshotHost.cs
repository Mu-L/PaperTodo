using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>
/// A short-lived, compact layered source used only while a real host changes from compact to its
/// final preview tree. It contains a frozen WPF bitmap of the compact shell; preview/WebView pixels
/// always remain live in the real host and are never copied through RenderTargetBitmap.
/// </summary>
internal sealed class EdgeCapsuleProxySnapshotHost : IDisposable
{
    private readonly Window _window;
    private bool _disposed;

    private EdgeCapsuleProxySnapshotHost(Window window)
    {
        _window = window;
    }

    public IntPtr Handle => _disposed
        ? IntPtr.Zero
        : new WindowInteropHelper(_window).Handle;

    public static EdgeCapsuleProxySnapshotHost? TryCreate(
        BitmapSource bitmap,
        EdgeCapsulePresentationFrame source)
    {
        if (source.Bounds.IsEmpty || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return null;
        }

        Window? window = null;
        try
        {
            var image = new Image
            {
                Source = bitmap,
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
                // This HWND is only a rasterization source. Keep it at the bottom until DComp has
                // wrapped and committed it, then the proxy session cloaks it. Creating the surface
                // from an already-cloaked window can yield an empty first raster on some systems.
                Topmost = false,
                Width = source.Bounds.Width / Math.Max(1, source.DpiScaleX),
                Height = source.Bounds.Height / Math.Max(1, source.DpiScaleY),
                Content = image,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            var handle = new WindowInteropHelper(window).EnsureHandle();
            if (handle == IntPtr.Zero ||
                !WindowNative.TrySetWindowDeviceBounds(window, source.Bounds))
            {
                window.Close();
                return null;
            }

            WindowNative.ApplyNoActivateStyle(window);
            WindowNative.SetInputPassthrough(window, enabled: true);
            window.Show();
            WindowNative.ApplyBottomZOrder(window);
            if (!WindowNative.TrySetWindowDeviceBounds(window, source.Bounds))
            {
                window.Close();
                return null;
            }
            window.UpdateLayout();
            WindowNative.FlushDesktopComposition();
            return new EdgeCapsuleProxySnapshotHost(window);
        }
        catch
        {
            try
            {
                window?.Close();
            }
            catch
            {
                // A failed optional source must not affect the real capsule host.
            }
            return null;
        }
    }

    public bool TrySetCloaked(bool cloaked) =>
        !_disposed && WindowNative.TrySetWindowCloaked(Handle, cloaked);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _window.Close();
        }
        catch
        {
            // The snapshot is disposable cover state; teardown is best effort.
        }
    }
}
