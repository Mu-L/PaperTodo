using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    internal static Task<CoreWebView2Environment> SharedPluginEnvironmentAsync(
        string pluginDirectory) =>
        GetPluginEnvironmentAsync(pluginDirectory);

    internal static string SharedWebHostName(string pluginId) =>
        WebHostName(pluginId);

    internal static bool AttachSharedBackgroundWebView(
        WebView2CompositionControl webView) =>
        BackgroundWebViewHost.TryAttach(webView);

    internal static void DetachSharedBackgroundWebView(
        WebView2CompositionControl webView) =>
        BackgroundWebViewHost.Detach(webView);
}
