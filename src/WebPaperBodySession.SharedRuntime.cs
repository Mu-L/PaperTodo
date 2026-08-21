using Microsoft.Web.WebView2.Core;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    internal static Task<CoreWebView2Environment> SharedPluginEnvironmentAsync(
        string pluginDirectory) =>
        GetPluginEnvironmentAsync(pluginDirectory);

    internal static string SharedWebHostName(string pluginId) =>
        WebHostName(pluginId);
}
