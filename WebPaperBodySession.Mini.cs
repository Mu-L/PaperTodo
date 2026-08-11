using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private WebPluginMiniViewHost? _miniViewHost;

    internal bool HasMiniEntry =>
        !_disposed &&
        !string.IsNullOrWhiteSpace(_manifest.MiniEntryPath);

    internal EdgeCapsulePreviewDescriptor DescribeMiniView(
        EdgeCapsulePreviewContext context,
        Func<EdgeCapsulePreviewContext, EdgeCapsulePreviewSize, FrameworkElement>
            buildFallback)
    {
        var declared = _manifest.MiniSize;
        var size = new EdgeCapsulePreviewSize(
            declared?.Width ?? 320,
            declared?.Height ?? 220);
        return new EdgeCapsulePreviewDescriptor(
            size,
            normalized => GetOrCreateMiniView(
                normalized,
                buildFallback(context, normalized)),
            visible => _miniViewHost?.SetVisible(visible),
            DeferContentCreation: true);
    }

    private FrameworkElement GetOrCreateMiniView(
        EdgeCapsulePreviewSize size,
        FrameworkElement fallback)
    {
        if (_miniViewHost == null || !_miniViewHost.Matches(size))
        {
            _miniViewHost?.Dispose();
            _miniViewHost = new WebPluginMiniViewHost(this, size, fallback);
        }
        else
        {
            _miniViewHost.ReplaceFallback(fallback);
        }
        return _miniViewHost;
    }

    private void UpdateStateFromWebSurface(
        JsonElement payload,
        WebPluginMiniViewHost? sourceMini)
    {
        var nextStateJson = payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : payload.GetRawText();
        if (string.Equals(nextStateJson, _stateJson, StringComparison.Ordinal))
        {
            return;
        }

        _context.SaveStateJson(nextStateJson);
        _stateJson = nextStateJson;
        if (sourceMini != null)
        {
            SendStateChanged();
        }
        if (!ReferenceEquals(_miniViewHost, sourceMini))
        {
            _miniViewHost?.SendStateChanged();
        }
    }

    private void SendStateChanged() => Send(new
    {
        type = "stateChanged",
        state = ParseState(_stateJson),
        stateVersion = _context.TargetStateVersion
    });

    private sealed class WebPluginMiniViewHost : Grid, IDisposable
    {
        private readonly WebPaperBodySession _owner;
        private readonly EdgeCapsulePreviewSize _size;
        private readonly WebView2CompositionControl _webView;
        private readonly CancellationTokenSource _lifetime = new();
        private FrameworkElement _fallback;
        private string _expectedOrigin = "";
        private bool _visible;
        private bool _initializationStarted;
        private bool _documentReady;
        private bool _pluginReportedReady;
        private bool _pluginReady;
        private bool _disposed;
        private int _documentGeneration;
        private ulong _documentNavigationId;
        private bool _hasDocumentNavigation;
        private int _queuedShowGeneration = -1;
        private int _readyProbeGeneration = -1;
        private string? _readyProbeToken;

        public WebPluginMiniViewHost(
            WebPaperBodySession owner,
            EdgeCapsulePreviewSize size,
            FrameworkElement fallback)
        {
            _owner = owner;
            _size = size;
            _fallback = fallback;
            PrepareFallbackForFirstDisplay(_fallback);
            Background = Brushes.Transparent;
            ClipToBounds = true;

            _webView = new WebView2CompositionControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            _webView.SetValue(UIElement.OpacityProperty, 0.0);
            PaperMiniViewInteraction.SetConsumesPointer(_webView, true);
            Children.Add(_fallback);
            Children.Add(_webView);
            Panel.SetZIndex(_webView, 2);

            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        public bool Matches(EdgeCapsulePreviewSize size) =>
            Math.Abs(_size.WidthDip - size.WidthDip) <= 0.001 &&
            Math.Abs(_size.HeightDip - size.HeightDip) <= 0.001;

        public void ReplaceFallback(FrameworkElement fallback)
        {
            if (ReferenceEquals(_fallback, fallback))
            {
                return;
            }
            Children.Remove(_fallback);
            _fallback = fallback;
            PrepareFallbackForFirstDisplay(_fallback);
            Children.Insert(0, fallback);
            _fallback.Visibility = _pluginReady
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private static void PrepareFallbackForFirstDisplay(FrameworkElement fallback)
        {
            if (fallback is EdgeCapsuleLivePreviewView livePreview)
            {
                livePreview.PrepareForFirstDisplay();
            }
        }

        public void SetVisible(bool visible)
        {
            if (_disposed)
            {
                return;
            }
            _visible = visible;
            if (visible)
            {
                TryStartInitialization();
                if (_pluginReportedReady)
                {
                    QueueShowPlugin();
                }
            }
            else
            {
                Send(new { type = "commitRequested" });
            }
            Send(new { type = "miniVisibilityChanged", visible });
            UpdatePresentation();
        }

        public void SendStateChanged() => Send(new
        {
            type = "stateChanged",
            state = ParseState(_owner._stateJson),
            stateVersion = _owner._context.TargetStateVersion
        });

        public void SendSettingsChanged() => Send(new
        {
            type = "settingsChanged",
            settings = ParseState(_owner._settingsJson)
        });

        public void SendThemeChanged(string type) => Send(new
        {
            type,
            theme = ThemePayload(_owner._theme)
        });

        private void OnLoaded(object sender, RoutedEventArgs e) =>
            TryStartInitialization();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
            TryStartInitialization();

        private void TryStartInitialization()
        {
            if (_initializationStarted ||
                _disposed ||
                !_visible ||
                !IsLoaded ||
                ActualWidth <= 0 ||
                ActualHeight <= 0)
            {
                return;
            }

            _initializationStarted = true;
            _ = InitializeAsync(_lifetime.Token);
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            try
            {
                var environment = await WebPaperBodySession.GetPluginEnvironmentAsync(
                    _owner._manifest.DirectoryPath);
                token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

                await _webView.EnsureCoreWebView2Async(environment);
                token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

                var core = _webView.CoreWebView2
                    ?? throw new InvalidOperationException(
                        "WebView2 initialization returned no CoreWebView2 instance.");
                core.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
                core.Settings.AreDevToolsEnabled = true;
#else
                core.Settings.AreDevToolsEnabled = false;
#endif
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;

                var hostName = WebHostName(_owner._manifest.Id);
                _expectedOrigin = $"https://{hostName}";
                var webRoot = Path.GetDirectoryName(_owner._manifest.EntryPath)
                    ?? throw new InvalidOperationException(
                        "Web plugin entry has no containing directory.");
                var relativeEntry = Path.GetRelativePath(
                        webRoot,
                        _owner._manifest.MiniEntryPath)
                    .Replace('\\', '/');
                var miniUri = new Uri(
                    $"{_expectedOrigin}/{Uri.EscapeDataString(relativeEntry).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");

                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.ProcessFailed += OnProcessFailed;
                core.DownloadStarting += WebPaperBodySession.OnDownloadStarting;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    BuildMiniBridgeScript(_expectedOrigin));
                token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

                core.SetVirtualHostNameToFolderMapping(
                    hostName,
                    webRoot,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                _webView.Source = miniUri;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch
            {
                ShowFallback();
            }
        }

        private static string BuildMiniBridgeScript(string expectedOrigin)
        {
            var originJson = JsonSerializer.Serialize(expectedOrigin);
            return $$"""
                (() => {
                  const expectedOrigin = {{originJson}};
                  if (window !== window.top || location.origin !== expectedOrigin || window.papertodo) return;
                  const listeners = new Set();
                  const pending = new Map();
                  let sequence = 0;
                  let stateProvider = null;
                  const post = (type, payload = null) => window.chrome.webview.postMessage({ type, payload });
                  const saveState = state => post('saveState', state ?? {});
                  const flushState = () => {
                    if (typeof stateProvider !== 'function') return;
                    try { saveState(stateProvider()); } catch { }
                  };
                  const request = (method, params = {}) => {
                    const requestId = `m${++sequence}`;
                    return new Promise((resolve, reject) => {
                      pending.set(requestId, { resolve, reject });
                      post('hostRequest', { requestId, method: String(method ?? ''), params: params ?? {} });
                    });
                  };
                  const paper = Object.freeze({
                    setTitle(title) { post('setTitle', String(title ?? '')); },
                    setHeaderText(text) { post('setHeaderText', String(text ?? '')); },
                    setCapsulePresentation(value) { post('setCapsulePresentation', value ?? null); }
                  });
                  const body = Object.freeze({
                    markDirty() { post('markDirty'); },
                    openExternal(url) { post('openExternal', String(url ?? '')); }
                  });
                  let miniReady = false;
                  const mini = Object.freeze({
                    ready() {
                      miniReady = true;
                      post('miniReady');
                    }
                  });
                  window.papertodo = Object.freeze({
                    surface: 'mini', paper, body, mini,
                    workspace: Object.freeze({ request }),
                    post, request, saveState, flushState,
                    registerStateProvider(provider) {
                      stateProvider = typeof provider === 'function' ? provider : null;
                      return () => { if (stateProvider === provider) stateProvider = null; };
                    },
                    onEvent(listener) {
                      if (typeof listener !== 'function') return () => {};
                      listeners.add(listener);
                      return () => listeners.delete(listener);
                    }
                  });
                  window.chrome.webview.addEventListener('message', event => {
                    const message = event.data;
                    if (message?.type === 'commitRequested') flushState();
                    if (message?.type === 'miniReadyProbe') {
                      post('miniReadyProbeResult', {
                        token: String(message.token ?? ''),
                        ready: miniReady
                      });
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
                  window.addEventListener('beforeunload', flushState);
                  document.addEventListener('visibilitychange', () => {
                    if (document.visibilityState === 'hidden') flushState();
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
                try
                {
                    _owner._context.OpenExternal(uri.AbsoluteUri);
                }
                catch
                {
                }
                return;
            }

            CancelQueuedShowPlugin();
            _documentGeneration++;
            _documentNavigationId = e.NavigationId;
            _hasDocumentNavigation = true;
            _documentReady = false;
            _pluginReportedReady = false;
            _pluginReady = false;
            ShowFallback();
        }

        private void OnNavigationCompleted(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
                !_hasDocumentNavigation ||
                e.NavigationId != _documentNavigationId)
            {
                // A host-cancelled external navigation still raises NavigationCompleted. It does
                // not replace the healthy mini document and must not tear its painted surface down.
                return;
            }
            if (!e.IsSuccess)
            {
                _documentReady = false;
                ShowFallback();
                return;
            }
            _documentReady = true;
            SendInitialize();
            // mini.ready() is allowed before NavigationCompleted. The current document's bridge
            // remembers that call. A challenge sent to the currently committed document recovers
            // it without allowing an old same-origin document to authorize the new generation.
            RequestMiniReadyProbe();
        }

        private void OnProcessFailed(
            object? sender,
            CoreWebView2ProcessFailedEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2))
            {
                return;
            }
            CancelQueuedShowPlugin();
            _documentGeneration++;
            _hasDocumentNavigation = false;
            _documentReady = false;
            _pluginReportedReady = false;
            _pluginReady = false;
            ShowFallback();
        }

        private void OnWebMessageReceived(
            object? sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
                !IsAllowedSource(e.Source))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    return;
                }
                var type = typeElement.GetString() ?? "";
                var payload = root.TryGetProperty("payload", out var value)
                    ? value
                    : default;
                switch (type)
                {
                    case "miniReady":
                        // Do not trust the source URL alone: a retiring same-origin document can
                        // still have a queued message. Challenge the currently committed document
                        // and promote only its answer.
                        RequestMiniReadyProbe();
                        break;
                    case "miniReadyProbeResult":
                        if (!_documentReady ||
                            _readyProbeGeneration != _documentGeneration ||
                            !string.Equals(
                                PayloadString(payload, "token"),
                                _readyProbeToken,
                                StringComparison.Ordinal) ||
                            !payload.TryGetProperty("ready", out var readyValue) ||
                            readyValue.ValueKind != JsonValueKind.True)
                        {
                            break;
                        }
                        _readyProbeGeneration = -1;
                        _readyProbeToken = null;
                        _pluginReportedReady = true;
                        QueueShowPlugin();
                        break;
                    case "saveState":
                        _owner.UpdateStateFromWebSurface(payload, this);
                        break;
                    case "setTitle":
                        _owner._context.SetTitle(ReadPayloadString(payload));
                        break;
                    case "setHeaderText":
                        _owner._context.Paper.SetHeaderText(ReadPayloadString(payload));
                        break;
                    case "setCapsulePresentation":
                        _owner._context.Paper.SetCapsulePresentation(
                            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                ? null
                                : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                    payload.GetRawText(),
                                    BridgeJsonOptions));
                        break;
                    case "markDirty":
                        _owner._context.MarkDirty();
                        break;
                    case "openExternal":
                        _owner._context.OpenExternal(ReadPayloadString(payload));
                        break;
                    case "hostRequest":
                        HandleHostRequest(payload);
                        break;
                }
            }
            catch
            {
                // A malformed mini message cannot affect the body session.
            }
        }

        private void HandleHostRequest(JsonElement payload)
        {
            var requestId = PayloadString(payload, "requestId");
            var generation = _documentGeneration;
            try
            {
                var method = PayloadString(payload, "method");
                var parameters = payload.ValueKind == JsonValueKind.Object &&
                                 payload.TryGetProperty("params", out var paramsValue)
                    ? paramsValue
                    : JsonSerializer.SerializeToElement(new { });
                var result = _owner.ExecuteHostRequest(method, parameters);
                if (generation != _documentGeneration)
                {
                    return;
                }
                Send(new { type = "hostResponse", requestId, ok = true, result });
            }
            catch (PaperTodoPluginException ex)
            {
                if (generation == _documentGeneration)
                {
                    Send(new
                    {
                        type = "hostResponse",
                        requestId,
                        ok = false,
                        error = new { code = ex.Code, message = ex.Message }
                    });
                }
            }
            catch
            {
                if (generation == _documentGeneration)
                {
                    Send(new
                    {
                        type = "hostResponse",
                        requestId,
                        ok = false,
                        error = new
                        {
                            code = "host_error",
                            message = "PaperTodo could not complete the plugin request."
                        }
                    });
                }
            }
        }

        private void SendInitialize() => Send(new
        {
            type = "initialize",
            surface = "mini",
            paperId = _owner._context.PaperId,
            providerId = _owner._context.ProviderId,
            apiVersion = _owner._context.ApiVersion,
            state = ParseState(_owner._stateJson),
            stateVersion = _owner._context.StateVersion,
            targetStateVersion = _owner._context.TargetStateVersion,
            settings = ParseState(_owner._settingsJson),
            permissions = _owner._context.GrantedPermissions
                .OrderBy(value => value)
                .ToArray(),
            theme = ThemePayload(_owner._theme),
            visible = _visible,
            presentationVisible = _visible
        });

        private void QueueShowPlugin()
        {
            if (!_documentReady ||
                !_pluginReportedReady ||
                !_visible ||
                _disposed)
            {
                return;
            }
            var generation = _documentGeneration;
            if (_queuedShowGeneration == generation)
            {
                return;
            }

            CancelQueuedShowPlugin();
            _queuedShowGeneration = generation;
            CompositionTarget.Rendering += OnCompositionRendering;
        }

        private void RequestMiniReadyProbe()
        {
            if (!_documentReady || _disposed)
            {
                return;
            }

            _readyProbeGeneration = _documentGeneration;
            _readyProbeToken = Guid.NewGuid().ToString("N");
            Send(new { type = "miniReadyProbe", token = _readyProbeToken });
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            var generation = _queuedShowGeneration;
            _queuedShowGeneration = -1;
            if (_disposed ||
                generation != _documentGeneration ||
                !_documentReady ||
                !_pluginReportedReady ||
                !_visible)
            {
                return;
            }

            // Do not replace the fallback from a DispatcherPriority.Render callback. This event is
            // the first real composition frame after the current document reported ready.
            _pluginReady = true;
            _fallback.Visibility = Visibility.Collapsed;
            UpdatePresentation();
        }

        private void CancelQueuedShowPlugin()
        {
            if (_queuedShowGeneration < 0)
            {
                return;
            }
            CompositionTarget.Rendering -= OnCompositionRendering;
            _queuedShowGeneration = -1;
        }

        private bool IsAllowedSource(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(
                uri.GetLeftPart(UriPartial.Authority),
                _expectedOrigin,
                StringComparison.OrdinalIgnoreCase);

        private void UpdatePresentation()
        {
            // The edge host owns the outgoing cross-fade. Keep the last painted Web frame visible
            // after miniVisibilityChanged(false); only input stops immediately. Hiding the WebView
            // here would manufacture an empty frame while the card is still shrinking.
            var painted = _documentReady && _pluginReady && !_disposed;
            _webView.SetValue(UIElement.OpacityProperty, painted ? 1.0 : 0.0);
            _webView.IsHitTestVisible = painted && _visible;
            if (!_pluginReady)
            {
                _fallback.Visibility = Visibility.Visible;
            }
        }

        private void ShowFallback()
        {
            if (_disposed)
            {
                return;
            }
            CancelQueuedShowPlugin();
            _readyProbeGeneration = -1;
            _readyProbeToken = null;
            _pluginReady = false;
            _pluginReportedReady = false;
            _fallback.Visibility = Visibility.Visible;
            _webView.SetValue(UIElement.OpacityProperty, 0.0);
            _webView.IsHitTestVisible = false;
        }

        private void Send(object value)
        {
            if (!_documentReady || _disposed || _webView.CoreWebView2 == null)
            {
                return;
            }
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(value, BridgeJsonOptions));
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            Send(new { type = "commitRequested" });
            _disposed = true;
            CancelQueuedShowPlugin();
            _lifetime.Cancel();
            Loaded -= OnLoaded;
            SizeChanged -= OnSizeChanged;
            if (_webView.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.ProcessFailed -= OnProcessFailed;
                core.DownloadStarting -= WebPaperBodySession.OnDownloadStarting;
            }
            Children.Remove(_webView);
            try
            {
                _webView.Dispose();
            }
            catch
            {
            }
            _lifetime.Dispose();
        }
    }
}
