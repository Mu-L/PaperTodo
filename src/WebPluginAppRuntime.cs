using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class WebPluginAppRuntime : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly IPaperTodoHostApi _workspace;
    private readonly PaperAppRuntimeGlobalTopBarApi _globalTopBar;
    private readonly Func<bool> _isActive;
    private readonly WebView2CompositionControl _webView;
    private readonly CancellationTokenSource _lifetime = new();
    private string _expectedOrigin = string.Empty;
    private bool _documentReady;
    private bool _disposed;

    public WebPluginAppRuntime(
        PaperBodyPluginDescriptor descriptor,
        IPaperTodoHostApi workspace,
        PaperAppRuntimeGlobalTopBarApi globalTopBar,
        Func<bool> isActive)
    {
        _descriptor = descriptor;
        _workspace = workspace;
        _globalTopBar = globalTopBar;
        _isActive = isActive;
        _webView = new WebView2CompositionControl
        {
            Width = 1,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 0
        };
        if (!WebPaperBodySession.AttachSharedBackgroundWebView(_webView))
        {
            throw new InvalidOperationException(
                "PaperTodo could not attach the Web plugin app runtime to its background host.");
        }
    }

    public async Task StartAsync()
    {
        ThrowIfInactive();
        var manifest = _descriptor.Manifest
            ?? throw new InvalidOperationException("Web app runtime manifest is unavailable.");
        var webRoot = Path.GetDirectoryName(manifest.EntryPath)
            ?? throw new InvalidOperationException("Web plugin entry has no containing directory.");
        var runtimePath = Path.Combine(webRoot, "runtime.html");
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException(
                "A Web plugin declaring appRuntime must provide runtime.html beside its body entry.",
                runtimePath);
        }

        var environment = await WebPaperBodySession.SharedPluginEnvironmentAsync(
            manifest.DirectoryPath);
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();
        await _webView.EnsureCoreWebView2Async(environment);
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();

        var core = _webView.CoreWebView2
            ?? throw new InvalidOperationException(
                "WebView2 initialization returned no CoreWebView2 instance.");
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        var hostName = WebPaperBodySession.SharedWebHostName(_descriptor.Id);
        _expectedOrigin = $"https://{hostName}";
        var relativeRuntime = Path.GetRelativePath(webRoot, runtimePath)
            .Replace('\\', '/');
        var runtimeUri = new Uri(
            $"{_expectedOrigin}/{Uri.EscapeDataString(relativeRuntime).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            BuildBridgeScript(_expectedOrigin));
        core.SetVirtualHostNameToFolderMapping(
            hostName,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        _webView.Source = runtimeUri;
    }

    private static string BuildBridgeScript(string expectedOrigin)
    {
        var originJson = JsonSerializer.Serialize(expectedOrigin);
        return $$"""
            (() => {
              const expectedOrigin = {{originJson}};
              if (window !== window.top || location.origin !== expectedOrigin || window.papertodo) return;
              const listeners = new Set();
              const pending = new Map();
              let sequence = 0;
              const post = (type, payload = null) => window.chrome.webview.postMessage({ type, payload });
              const request = (method, params = {}) => {
                const requestId = `a${++sequence}`;
                return new Promise((resolve, reject) => {
                  pending.set(requestId, { resolve, reject });
                  post('hostRequest', { requestId, method: String(method ?? ''), params: params ?? {} });
                });
              };
              const workspace = Object.freeze({ request });
              const globalTopBar = Object.freeze({
                setActions(actions) {
                  return request('topbar.global.set', {
                    actions: Array.isArray(actions) ? actions : []
                  });
                }
              });
              window.papertodo = Object.freeze({
                surface: 'app',
                workspace,
                globalTopBar,
                request,
                onEvent(listener) {
                  if (typeof listener !== 'function') return () => {};
                  listeners.add(listener);
                  return () => listeners.delete(listener);
                }
              });
              window.chrome.webview.addEventListener('message', event => {
                const message = event.data;
                if (message?.type === 'hostResponse') {
                  const waiter = pending.get(message.requestId);
                  if (waiter) {
                    pending.delete(message.requestId);
                    if (message.ok) waiter.resolve(message.result);
                    else {
                      const error = new Error(message.error?.message ?? 'PaperTodo host request failed.');
                      error.code = message.error?.code ?? 'host_error';
                      waiter.reject(error);
                    }
                  }
                }
                for (const listener of [...listeners]) {
                  try { listener(message); } catch { }
                }
                window.dispatchEvent(new CustomEvent('papertodo', { detail: message }));
              });
            })();
            """;
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }
        if (!string.IsNullOrEmpty(_expectedOrigin) &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            !string.Equals(
                uri.GetLeftPart(UriPartial.Authority),
                _expectedOrigin,
                StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            return;
        }
        _documentReady = false;
        TryClearGlobalTopBar();
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !e.IsSuccess ||
            !IsAllowedSource(_webView.Source?.AbsoluteUri))
        {
            _documentReady = false;
            TryClearGlobalTopBar();
            return;
        }
        _documentReady = true;
        Send(new
        {
            type = "initialize",
            surface = "app",
            providerId = _descriptor.Id,
            apiVersion = _descriptor.ApiVersion,
            permissions = _workspace.GrantedPermissions.OrderBy(value => value).ToArray()
        });
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }
        _documentReady = false;
        TryClearGlobalTopBar();
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !_documentReady ||
            !IsAllowedSource(e.Source))
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeValue) ||
                typeValue.ValueKind != JsonValueKind.String ||
                !string.Equals(typeValue.GetString(), "hostRequest", StringComparison.Ordinal))
            {
                return;
            }
            HandleHostRequest(
                root.TryGetProperty("payload", out var payload)
                    ? payload
                    : default);
        }
        catch
        {
            // Malformed app-runtime messages stay isolated to that runtime.
        }
    }

    private void HandleHostRequest(JsonElement payload)
    {
        var requestId = RequiredString(payload, "requestId");
        try
        {
            var method = RequiredString(payload, "method");
            var parameters = payload.ValueKind == JsonValueKind.Object &&
                             payload.TryGetProperty("params", out var paramsValue)
                ? paramsValue
                : JsonSerializer.SerializeToElement(new { });
            object? result;
            if (string.Equals(method, "topbar.global.set", StringComparison.Ordinal))
            {
                result = SetGlobalActions(parameters);
            }
            else
            {
                result = WebPluginWorkspaceRequests.Execute(
                    _workspace,
                    method,
                    parameters);
            }
            Send(new { type = "hostResponse", requestId, ok = true, result });
        }
        catch (PaperTodoPluginException ex)
        {
            Send(new
            {
                type = "hostResponse",
                requestId,
                ok = false,
                error = new { code = ex.Code, message = ex.Message }
            });
        }
        catch
        {
            Send(new
            {
                type = "hostResponse",
                requestId,
                ok = false,
                error = new
                {
                    code = "host_error",
                    message = "PaperTodo could not complete the plugin app-runtime request."
                }
            });
        }
    }

    private object SetGlobalActions(JsonElement parameters)
    {
        PaperTopBarAction[] actions;
        try
        {
            actions = parameters.ValueKind == JsonValueKind.Object &&
                      parameters.TryGetProperty("actions", out var actionsValue) &&
                      actionsValue.ValueKind != JsonValueKind.Null
                ? actionsValue.Deserialize<PaperTopBarAction[]>(JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new PaperTodoPluginException(
                "invalid_params",
                ex.GetBaseException().Message);
        }

        _globalTopBar.SetActionHandler(invocation =>
            Send(new { type = "topBarActionInvoked", action = invocation }));
        _globalTopBar.SetActions(actions);
        return new { updated = actions.Length };
    }

    private static string RequiredString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} is required.");
        }
        return value.GetString()!;
    }

    private bool IsAllowedSource(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority),
            _expectedOrigin,
            StringComparison.OrdinalIgnoreCase);

    private void Send(object value)
    {
        if (!_documentReady || _disposed || !_isActive() || _webView.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(value, JsonOptions));
        }
        catch
        {
        }
    }

    private void TryClearGlobalTopBar()
    {
        try { _globalTopBar.Clear(); } catch { }
    }

    private void ThrowIfInactive()
    {
        if (_disposed || !_isActive())
        {
            throw new InvalidOperationException("The plugin app runtime is no longer active.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _documentReady = false;
        TryClearGlobalTopBar();
        _disposed = true;
        _lifetime.Cancel();
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.ProcessFailed -= OnProcessFailed;
        }
        WebPaperBodySession.DetachSharedBackgroundWebView(_webView);
        try { _webView.Dispose(); } catch { }
        _lifetime.Dispose();
    }
}
