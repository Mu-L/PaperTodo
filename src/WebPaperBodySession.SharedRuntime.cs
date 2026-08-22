using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo;

// These narrow accessors keep the large body-session implementation private while allowing the
// shared Web runtime infrastructure below to reuse the same environment pool/background host.
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

/// <summary>
/// Common non-surface-specific Web plugin runtime services. Body, mini and provider app-runtime
/// code should not each invent origin/URI/serialization/environment rules. The body session still
/// owns its visual lifecycle; this class owns only the shared transport/runtime policy.
/// </summary>
internal static class WebPluginRuntimeInfrastructure
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Task<CoreWebView2Environment> EnvironmentAsync(string pluginDirectory) =>
        WebPaperBodySession.SharedPluginEnvironmentAsync(pluginDirectory);

    public static bool AttachBackground(WebView2CompositionControl webView) =>
        WebPaperBodySession.AttachSharedBackgroundWebView(webView);

    public static void DetachBackground(WebView2CompositionControl webView) =>
        WebPaperBodySession.DetachSharedBackgroundWebView(webView);

    public static string Origin(string pluginId) =>
        $"https://{WebPaperBodySession.SharedWebHostName(pluginId)}";

    public static string HostName(string pluginId) =>
        WebPaperBodySession.SharedWebHostName(pluginId);

    public static Uri LocalEntryUri(
        string expectedOrigin,
        string webRoot,
        string entryPath)
    {
        var relative = Path.GetRelativePath(webRoot, entryPath).Replace('\\', '/');
        return new Uri(
            $"{expectedOrigin}/{Uri.EscapeDataString(relative).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");
    }

    public static bool IsSameOrigin(string? value, string expectedOrigin) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority),
            expectedOrigin,
            StringComparison.OrdinalIgnoreCase);

    public static void ConfigureBackgroundCore(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
    }

    public static string RequiredString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PaperTodo.Plugin.PaperTodoPluginException(
                "invalid_params",
                $"{name} is required.");
        }
        return value.GetString()!;
    }

    public static JsonElement ParametersOrEmpty(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("params", out var paramsValue)
            ? paramsValue
            : JsonSerializer.SerializeToElement(new { });
}
