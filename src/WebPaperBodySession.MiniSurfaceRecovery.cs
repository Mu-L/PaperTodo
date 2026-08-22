using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    // WebView2CompositionControl can retain an initialized document while its capture-backed WPF
    // surface loses the last frame after the mini host leaves and later rejoins the visual tree.
    // A normally ticking/animated page repairs that state by producing another browser frame, but a
    // static or paused mini has no reason to repaint and can therefore remain transparent forever.
    // Keep this recovery host-owned: on a warm re-Load, briefly add/remove one almost-invisible
    // pixel so Chromium must submit fresh damage without reloading the document or touching plugin
    // state. The first cold Load has no CoreWebView2 yet and deliberately bypasses this path.
    private const int MiniSurfaceRecoverySettleMilliseconds = 34;

    private static readonly ConditionalWeakTable<FrameworkElement, MiniSurfaceRecoveryState>
        MiniSurfaceRecoveryStates = new();

    private sealed class MiniSurfaceRecoveryState
    {
        public int Generation;
    }

    [ModuleInitializer]
    internal static void RegisterMiniSurfaceRecovery()
    {
        EventManager.RegisterClassHandler(
            typeof(WebPluginMiniViewHost),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWebMiniSurfaceLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(WebPluginMiniViewHost),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnWebMiniSurfaceUnloaded),
            handledEventsToo: true);
    }

    private static void OnWebMiniSurfaceUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host)
        {
            return;
        }

        var state = MiniSurfaceRecoveryStates.GetOrCreateValue(host);
        unchecked
        {
            state.Generation++;
        }
    }

    private static async void OnWebMiniSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebPluginMiniViewHost host)
        {
            return;
        }

        WebView2CompositionControl? webView = null;
        foreach (UIElement child in host.Children)
        {
            if (child is WebView2CompositionControl candidate)
            {
                webView = candidate;
                break;
            }
        }

        var core = webView?.CoreWebView2;
        if (core == null || webView?.Source == null)
        {
            // Cold mini initialization starts only after the first Loaded event, so there is no
            // stale capture surface to recover on that path.
            return;
        }

        var state = MiniSurfaceRecoveryStates.GetOrCreateValue(host);
        int generation;
        unchecked
        {
            generation = ++state.Generation;
        }

        var markerId = $"__papertodo-mini-repaint-{Guid.NewGuid():N}";
        var markerJson = JsonSerializer.Serialize(markerId);
        var addMarkerScript = $$"""
            (() => {
              const id = {{markerJson}};
              document.getElementById(id)?.remove();
              const parent = document.body || document.documentElement;
              if (!parent) return false;
              const marker = document.createElement('i');
              marker.id = id;
              marker.setAttribute('aria-hidden', 'true');
              marker.style.cssText =
                'position:fixed;left:0;top:0;width:1px;height:1px;' +
                'margin:0;padding:0;border:0;pointer-events:none;' +
                'z-index:2147483647;background:#fff;opacity:.02;';
              parent.appendChild(marker);
              return true;
            })();
            """;
        var removeMarkerScript = $$"""
            (() => {
              document.getElementById({{markerJson}})?.remove();
              return true;
            })();
            """;

        try
        {
            // The first mutation forces browser-side paint damage. Keep it alive across roughly two
            // 60 Hz frames so the composition-control capture path has time to observe a committed
            // frame even when the plugin itself is completely static.
            await core.ExecuteScriptAsync(addMarkerScript);
            await Task.Delay(MiniSurfaceRecoverySettleMilliseconds);
        }
        catch
        {
            // Process/navigation failure continues through the existing Web mini fallback paths.
        }
        finally
        {
            try
            {
                await core.ExecuteScriptAsync(removeMarkerScript);
            }
            catch
            {
            }
        }

        if (!host.IsLoaded || generation != state.Generation)
        {
            return;
        }

        try
        {
            // Removing the marker is the second damage event. Let it settle as well, then invalidate
            // only the WPF wrapper so its next composition pass samples the refreshed capture image.
            await Task.Delay(MiniSurfaceRecoverySettleMilliseconds);
            if (host.IsLoaded && generation == state.Generation)
            {
                webView.InvalidateVisual();
            }
        }
        catch
        {
        }
    }
}
