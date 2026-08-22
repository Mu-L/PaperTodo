using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class WebPluginAppRuntime : IDisposable
{
    private const int MaximumGlobalTopBarActions = 256;

    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly IPaperTodoHostApi _workspace;
    private readonly IPaperAppRuntimeSettings _settings;
    private readonly PaperAppRuntimeGlobalTopBarApi _globalTopBar;
    private readonly PaperAppRuntimeGlobalShortcutApi _globalShortcuts;
    private readonly Func<bool> _isActive;
    private readonly Action _requestRestart;
    private readonly WebView2CompositionControl _webView;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _startupReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _expectedOrigin = string.Empty;
    private bool _documentReady;
    private ulong _documentNavigationId;
    private bool _hasDocumentNavigation;
    private bool _reloadRecoveryPending;
    private bool _restartRequested;
    private bool _startupCompleted;
    private bool _disposed;

    public WebPluginAppRuntime(
        PaperBodyPluginDescriptor descriptor,
        IPaperTodoHostApi workspace,
        IPaperAppRuntimeSettings settings,
        PaperAppRuntimeGlobalTopBarApi globalTopBar,
        PaperAppRuntimeGlobalShortcutApi globalShortcuts,
        Func<bool> isActive,
        Action requestRestart)
    {
        _descriptor = descriptor;
        _workspace = workspace;
        _settings = settings;
        _globalTopBar = globalTopBar;
        _globalShortcuts = globalShortcuts;
        _isActive = isActive;
        _requestRestart = requestRestart;
        _webView = new WebView2CompositionControl
        {
            Width = 1,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _webView.SetValue(UIElement.OpacityProperty, 0.0);
        if (!WebPluginRuntimeInfrastructure.AttachBackground(_webView))
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
        var runtimePath = manifest.RuntimePath;
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new InvalidOperationException(
                "The Web app runtime entry was not resolved during plugin discovery.");
        }

        var environment = await WebPluginRuntimeInfrastructure.EnvironmentAsync(
            manifest.DirectoryPath);
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();
        await _webView.EnsureCoreWebView2Async(environment);
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();

        var core = _webView.CoreWebView2
            ?? throw new InvalidOperationException(
                "WebView2 initialization returned no CoreWebView2 instance.");
        WebPluginRuntimeInfrastructure.ConfigureBackgroundCore(core);

        var hostName = WebPluginRuntimeInfrastructure.HostName(_descriptor.Id);
        _expectedOrigin = WebPluginRuntimeInfrastructure.Origin(_descriptor.Id);
        var runtimeUri = WebPluginRuntimeInfrastructure.LocalEntryUri(
            _expectedOrigin,
            webRoot,
            runtimePath);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            BuildBridgeScript(_expectedOrigin));
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();
        core.SetVirtualHostNameToFolderMapping(
            hostName,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        _webView.Source = runtimeUri;

        // A runtime is not Running merely because Source was assigned. Wait until the matching
        // local document has completed and the host has sent initialize, so bridge requests cannot
        // disappear into the pre-navigation gap.
        await _startupReady.Task.WaitAsync(_lifetime.Token);
        _startupCompleted = true;
        ThrowIfInactive();
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
              const queuedRequests = [];
              let sequence = 0;
              let hostReady = false;
              const post = (type, payload = null) => window.chrome.webview.postMessage({ type, payload });
              const postRequest = payload => {
                if (hostReady) post('hostRequest', payload);
                else queuedRequests.push(payload);
              };
              const request = (method, params = {}) => {
                const requestId = `a${++sequence}`;
                return new Promise((resolve, reject) => {
                  pending.set(requestId, { resolve, reject });
                  postRequest({ requestId, method: String(method ?? ''), params: params ?? {} });
                });
              };
              const workspace = Object.freeze({ request });
              const settings = Object.freeze({
                get() { return request('settings.get'); }
              });
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
                settings,
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
                if (message?.type === 'initialize' && !hostReady) {
                  hostReady = true;
                  for (const payload of queuedRequests.splice(0)) {
                    post('hostRequest', payload);
                  }
                }
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

        _documentNavigationId = e.NavigationId;
        _hasDocumentNavigation = true;
        _documentReady = false;
        TryClearGlobalTopBar();
        TryClearGlobalShortcuts();
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !_hasDocumentNavigation ||
            e.NavigationId != _documentNavigationId)
        {
            // A host-cancelled external navigation still raises NavigationCompleted. It never
            // became the committed app-runtime navigation and must not tear down the healthy page.
            return;
        }

        _hasDocumentNavigation = false;
        if (!e.IsSuccess || !IsAllowedSource(_webView.Source?.AbsoluteUri))
        {
            _reloadRecoveryPending = false;
            _documentReady = false;
            TryClearGlobalTopBar();
            TryClearGlobalShortcuts();
            FailStartupOrRestart(
                $"Web app runtime navigation failed ({e.WebErrorStatus}).");
            return;
        }

        _reloadRecoveryPending = false;
        _documentReady = true;
        RegisterGlobalShortcutHandler();
        Send(new
        {
            type = "initialize",
            surface = "app",
            providerId = _descriptor.Id,
            apiVersion = _descriptor.ApiVersion,
            permissions = _workspace.GrantedPermissions.OrderBy(value => value).ToArray(),
            settings = ReadSettings()
        });
        _startupReady.TrySetResult(true);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) || _disposed)
        {
            return;
        }

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The control is closed after a browser-process exit and cannot be recovered with
                // Reload. Let the app-runtime owner dispose this instance and create a fresh view.
                FailStartupOrRestart("The WebView2 browser process exited.");
                return;

            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                RecoverRendererByReload();
                return;

            default:
                // Utility/GPU/sandbox/subframe process failures are non-fatal to the top-level app
                // runtime. WebView2 recreates those processes or leaves the main document usable.
                return;
        }
    }

    private void RecoverRendererByReload()
    {
        if (_disposed || _restartRequested || _reloadRecoveryPending)
        {
            return;
        }

        _reloadRecoveryPending = true;
        _documentReady = false;
        TryClearGlobalTopBar();
        TryClearGlobalShortcuts();

        var dispatcher = _webView.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            FailStartupOrRestart("The Web app runtime dispatcher is shutting down.");
            return;
        }

        // Leave the ProcessFailed callback before calling back into WebView2. A repeated
        // RenderProcessUnresponsive event is ignored while this one recovery navigation is pending.
        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_disposed || _restartRequested || !_reloadRecoveryPending)
                {
                    return;
                }
                try
                {
                    var core = _webView.CoreWebView2;
                    if (core == null)
                    {
                        FailStartupOrRestart("The Web app runtime renderer could not be reloaded.");
                        return;
                    }
                    core.Reload();
                }
                catch (Exception ex)
                {
                    FailStartupOrRestart(
                        $"The Web app runtime renderer reload failed: {ex.GetBaseException().Message}");
                }
            }),
            DispatcherPriority.Background);
    }

    private void FailStartupOrRestart(string message)
    {
        _hasDocumentNavigation = false;
        _reloadRecoveryPending = false;
        _documentReady = false;
        TryClearGlobalTopBar();
        TryClearGlobalShortcuts();

        if (!_startupCompleted &&
            _startupReady.TrySetException(new InvalidOperationException(message)))
        {
            return;
        }

        RequestRestart();
    }

    private void RequestRestart()
    {
        if (_disposed || _restartRequested)
        {
            return;
        }

        _restartRequested = true;
        _hasDocumentNavigation = false;
        _reloadRecoveryPending = false;
        _documentReady = false;
        TryClearGlobalTopBar();
        TryClearGlobalShortcuts();
        try
        {
            _requestRestart();
        }
        catch
        {
            // Runtime ownership still tears this instance down during normal host shutdown.
        }
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
        var requestId = WebPluginRuntimeInfrastructure.RequiredString(payload, "requestId");
        try
        {
            var method = WebPluginRuntimeInfrastructure.RequiredString(payload, "method");
            var parameters = WebPluginRuntimeInfrastructure.ParametersOrEmpty(payload);
            object? result;
            if (string.Equals(method, "settings.get", StringComparison.Ordinal))
            {
                result = ReadSettings();
            }
            else if (string.Equals(method, "topbar.global.set", StringComparison.Ordinal))
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

    private JsonElement ReadSettings()
    {
        using var document = JsonDocument.Parse(_settings.Json);
        return document.RootElement.Clone();
    }

    private object SetGlobalActions(JsonElement parameters)
    {
        PaperTopBarAction[] actions;
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("actions", out var actionsValue) ||
                actionsValue.ValueKind == JsonValueKind.Null)
            {
                actions = [];
            }
            else
            {
                if (actionsValue.ValueKind == JsonValueKind.Array &&
                    actionsValue.GetArrayLength() > MaximumGlobalTopBarActions)
                {
                    throw new PaperTodoPluginException(
                        "too_many_topbar_actions",
                        $"A plugin can contribute at most {MaximumGlobalTopBarActions} global top-bar actions.");
                }

                actions = actionsValue.Deserialize<PaperTopBarAction[]>(
                    WebPluginRuntimeInfrastructure.JsonOptions) ?? [];
            }
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

    private void RegisterGlobalShortcutHandler()
    {
        _globalShortcuts.SetActionHandler(invocation =>
            Send(new
            {
                type = "shortcutInvoked",
                settingId = invocation.SettingId,
                actionId = invocation.ActionId
            }));
    }

    private bool IsAllowedSource(string? value) =>
        WebPluginRuntimeInfrastructure.IsSameOrigin(value, _expectedOrigin);

    private void Send(object value)
    {
        if (!_documentReady || _disposed || !_isActive() || _webView.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(
                    value,
                    WebPluginRuntimeInfrastructure.JsonOptions));
        }
        catch
        {
        }
    }

    private void TryClearGlobalTopBar()
    {
        try { _globalTopBar.Clear(); } catch { }
    }

    private void TryClearGlobalShortcuts()
    {
        try { _globalShortcuts.Clear(); } catch { }
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
        TryClearGlobalShortcuts();
        _disposed = true;
        _startupReady.TrySetCanceled();
        _lifetime.Cancel();
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.ProcessFailed -= OnProcessFailed;
        }
        WebPluginRuntimeInfrastructure.DetachBackground(_webView);
        try { _webView.Dispose(); } catch { }
        _lifetime.Dispose();
    }
}
