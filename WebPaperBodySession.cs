using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

// Web plugins are trusted. WebView2 keeps its normal navigation, frame, popup and permission
// behavior; only PaperTodo's host bridge remains restricted to the plugin's local top-level origin.
internal sealed class WebPaperBodySession : IPaperBodySession
{
    private static readonly object EnvironmentGate = new();
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks =
        new(StringComparer.OrdinalIgnoreCase);

    // A dedicated, non-activating runtime surface is used only for Web plugins that explicitly
    // require background updates and have never been presented. The real PaperWindow is never
    // moved or shown for prewarming.
    private static class BackgroundWebViewHost
    {
        private static Window? _window;
        private static Grid? _root;

        public static bool TryAttach(WebView2CompositionControl webView)
        {
            try
            {
                Application.Current.Dispatcher.VerifyAccess();
                if (webView.Parent is Panel parent)
                {
                    parent.Children.Remove(webView);
                }
                else if (webView.Parent != null)
                {
                    return false;
                }

                EnsureWindow();
                _root!.Children.Add(webView);
                webView.Width = 1;
                webView.Height = 1;
                webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                webView.VerticalAlignment = VerticalAlignment.Stretch;
                if (_window!.IsVisible == false)
                {
                    _window.Show();
                }
                return true;
            }
            catch
            {
                if (_root?.Children.Contains(webView) == true)
                {
                    _root.Children.Remove(webView);
                }
                return false;
            }
        }

        public static void Detach(WebView2CompositionControl webView)
        {
            if (_root?.Children.Contains(webView) == true)
            {
                _root.Children.Remove(webView);
            }
            if (_root?.Children.Count == 0 && _window?.IsVisible == true)
            {
                _window.Hide();
            }
        }

        public static bool Contains(WebView2CompositionControl webView) =>
            _root?.Children.Contains(webView) == true;

        private static void EnsureWindow()
        {
            if (_window != null)
            {
                return;
            }

            _root = new Grid
            {
                Width = 1,
                Height = 1,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };
            _window = new Window
            {
                Content = _root,
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Opacity = 0.01,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false
            };
        }
    }

    private readonly PaperBodyContext _context;
    private readonly PaperBodyPluginManifest _manifest;
    private readonly Grid _root;
    private WebView2CompositionControl _webView;
    private readonly CancellationTokenSource _lifetime = new();
    private int _webViewGeneration;
    private PaperBodyTheme _theme;
    private string _stateJson;
    private string _settingsJson;
    private string _expectedOrigin = "";
    private Uri? _entryUri;
    private bool _initializationStarted;
    private bool _initialized;
    private bool _documentReady;
    private bool _pluginDocumentReady;
    private bool _disposed;
    private bool _runtimeVisible;
    private bool _presentationVisible;
    private bool _everPresented;
    private bool _webViewFailed;

    public WebPaperBodySession(
        PaperBodyContext context,
        PaperBodyPluginManifest manifest)
    {
        _context = context;
        _manifest = manifest;
        _theme = context.Theme;
        _stateJson = context.StateJson;
        _settingsJson = context.SettingsJson;
        _root = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _webView = CreateWebView();
        _root.Children.Add(BuildStatusView(Strings.Get("PluginsWebLoading")));
        _root.Children.Add(_webView);
        Panel.SetZIndex(_webView, 1);
    }

    public FrameworkElement View => _root;

    private WebView2CompositionControl CreateWebView()
    {
        var webView = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        webView.SetValue(UIElement.OpacityProperty, 0.0);
        webView.Loaded += OnWebViewLoaded;
        webView.SizeChanged += OnWebViewSizeChanged;
        return webView;
    }

    private void OnWebViewLoaded(object sender, RoutedEventArgs e) =>
        TryStartInitialization();

    private void OnWebViewSizeChanged(object sender, SizeChangedEventArgs e) =>
        TryStartInitialization();

    private void TryStartInitialization()
    {
        var webView = _webView;
        var generation = _webViewGeneration;
        if (_initializationStarted ||
            _webViewFailed ||
            !_runtimeVisible ||
            _disposed ||
            !webView.IsLoaded ||
            webView.ActualWidth <= 0 ||
            webView.ActualHeight <= 0)
        {
            return;
        }

        _initializationStarted = true;
        _ = InitializeAsync(webView, generation, _lifetime.Token);
    }

    private async Task InitializeAsync(
        WebView2CompositionControl webView,
        int generation,
        CancellationToken token)
    {
        try
        {
            var environment = await GetPluginEnvironmentAsync(_manifest.DirectoryPath);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            await webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            var core = webView.CoreWebView2
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

            var hostName = WebHostName(_manifest.Id);
            _expectedOrigin = $"https://{hostName}";
            var webRoot = Path.GetDirectoryName(_manifest.EntryPath)
                ?? throw new InvalidOperationException("Web plugin entry has no containing directory.");
            var relativeEntry = Path.GetRelativePath(
                    webRoot,
                    _manifest.EntryPath)
                .Replace('\\', '/');
            _entryUri = new Uri(
                $"{_expectedOrigin}/{Uri.EscapeDataString(relativeEntry).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");

            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.DownloadStarting += OnDownloadStarting;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildBridgeScript(_expectedOrigin));
            token.ThrowIfCancellationRequested();
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                hostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Set readiness before navigation. A tiny local document can complete synchronously
            // enough for NavigationCompleted to run before the line after Source assignment.
            _initialized = true;
            _documentReady = false;
            webView.Source = _entryUri;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            _initializationStarted = false;
            ShowFailure(ex.GetBaseException().Message);
        }
    }

    private bool IsCurrentWebView(
        WebView2CompositionControl webView,
        int generation) =>
        !_disposed &&
        generation == _webViewGeneration &&
        ReferenceEquals(webView, _webView);

    private static string BuildBridgeScript(string expectedOrigin)
    {
        var originJson = JsonSerializer.Serialize(expectedOrigin);
        return $$"""
            (() => {
              const expectedOrigin = {{originJson}};
              if (window !== window.top || location.origin !== expectedOrigin || window.papertodo) return;
              const listeners = new Set();
              let stateProvider = null;
              const post = (type, payload = null) => {
                window.chrome.webview.postMessage({ type, payload });
              };
              const saveState = state => post('saveState', state ?? {});
              const flushState = () => {
                if (typeof stateProvider !== 'function') return;
                try { saveState(stateProvider()); } catch { }
              };
              window.papertodo = Object.freeze({
                post,
                saveState,
                flushState,
                registerStateProvider(provider) {
                  stateProvider = typeof provider === 'function' ? provider : null;
                  return () => { if (stateProvider === provider) stateProvider = null; };
                },
                setTitle(title) { post('setTitle', String(title ?? '')); },
                setDisplayTitle(text) { post('setDisplayTitle', String(text ?? '')); },
                setCapsuleText(text) { post('setDisplayTitle', String(text ?? '')); },
                setInputClaims(claims) {
                  const values = Array.isArray(claims)
                    ? claims.map(value => String(value ?? '')).filter(Boolean)
                    : [];
                  post('setInputClaims', values);
                },
                markDirty() { post('markDirty'); },
                openExternal(url) { post('openExternal', String(url ?? '')); },
                onEvent(listener) {
                  if (typeof listener !== 'function') return () => {};
                  listeners.add(listener);
                  return () => listeners.delete(listener);
                }
              });
              window.chrome.webview.addEventListener('message', event => {
                if (event.data?.type === 'commitRequested') flushState();
                for (const listener of [...listeners]) {
                  try { listener(event.data); } catch { }
                }
                window.dispatchEvent(new CustomEvent('papertodo', { detail: event.data }));
              });
              window.addEventListener('beforeunload', flushState);
              document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'hidden') flushState();
              });
            })();
            """;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }

        _documentReady = false;
        _pluginDocumentReady = false;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }

        if (!e.IsSuccess)
        {
            ShowFailure(
                $"{Strings.Get("PluginsWebNavigationFailed")} ({e.WebErrorStatus})");
            return;
        }

        _documentReady = true;
        _pluginDocumentReady = IsAllowedDocumentUri(_webView.Source?.AbsoluteUri);
        ShowWebView();
        if (_pluginDocumentReady)
        {
            SendInitialize();
        }
    }

    private static void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var value = e.DownloadOperation.Uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            // blob:, data: and other session-local downloads stay inside WebView2.
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Cancel = true;
        }
        catch
        {
            // If the default browser could not be launched, keep WebView2's normal download.
        }
    }

    private bool IsAllowedDocumentUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), _expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowWebView()
    {
        // A cold background runtime has no visual content in the paper body yet. Keep its
        // loading placeholder until first presentation replaces the background controller.
        if (!ReferenceEquals(_webView.Parent, _root))
        {
            UpdateWebViewPresentation();
            return;
        }

        for (var index = _root.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_root.Children[index], _webView))
            {
                _root.Children.RemoveAt(index);
            }
        }
        UpdateWebViewPresentation();
    }

    private void AttachWebViewToRoot()
    {
        BackgroundWebViewHost.Detach(_webView);
        if (_webView.Parent is Panel current &&
            !ReferenceEquals(current, _root))
        {
            current.Children.Remove(_webView);
        }

        _webView.Width = double.NaN;
        _webView.Height = double.NaN;
        _webView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _webView.VerticalAlignment = VerticalAlignment.Stretch;
        if (!_root.Children.Contains(_webView))
        {
            _root.Children.Add(_webView);
        }
        Panel.SetZIndex(_webView, 1);
    }

    private void PromoteBackgroundWebView()
    {
        if (!BackgroundWebViewHost.Contains(_webView))
        {
            AttachWebViewToRoot();
            return;
        }

        // An uninitialized control can move safely. Once a CoreWebView2 controller exists (or is
        // being created), replace it instead of reparenting it across HWNDs.
        if (!_initializationStarted && !_initialized)
        {
            AttachWebViewToRoot();
            return;
        }

        var previous = _webView;
        _webViewGeneration++;
        BackgroundWebViewHost.Detach(previous);
        DisposeWebView(previous);
        _context.SetInputClaims(PaperBodyInputClaims.None);

        _webView = CreateWebView();
        _initializationStarted = false;
        _initialized = false;
        _documentReady = false;
        _pluginDocumentReady = false;
        _webViewFailed = false;
        EnsureLoadingView();
        AttachWebViewToRoot();
    }

    private void EnsureLoadingView()
    {
        for (var index = _root.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_root.Children[index], _webView))
            {
                _root.Children.RemoveAt(index);
            }
        }
        _root.Children.Insert(
            0,
            BuildStatusView(Strings.Get("PluginsWebLoading")));
    }

    private void UpdateWebViewHost()
    {
        if (_disposed || _webViewFailed)
        {
            return;
        }

        if (_presentationVisible)
        {
            _everPresented = true;
            PromoteBackgroundWebView();
        }
        else if (_runtimeVisible &&
                 !_everPresented &&
                 !_initializationStarted &&
                 !_initialized &&
                 !BackgroundWebViewHost.Contains(_webView))
        {
            // Cold folded sessions that opted into background updates use the dedicated host.
            // After the first real presentation the WebView remains in the paper visual tree.
            _ = BackgroundWebViewHost.TryAttach(_webView);
        }

        UpdateWebViewPresentation();
        TryStartInitialization();
    }

    private void UpdateWebViewPresentation()
    {
        var inBackgroundHost = BackgroundWebViewHost.Contains(_webView);
        var inPaperBody = ReferenceEquals(_webView.Parent, _root);
        var show = _presentationVisible &&
            _documentReady &&
            !_disposed &&
            inPaperBody;
        _webView.SetValue(
            UIElement.OpacityProperty,
            show || (inBackgroundHost && !_webViewFailed) ? 1.0 : 0.0);
        _webView.IsHitTestVisible = show;
    }

    private void DisposeWebView(WebView2CompositionControl webView)
    {
        webView.Loaded -= OnWebViewLoaded;
        webView.SizeChanged -= OnWebViewSizeChanged;
        BackgroundWebViewHost.Detach(webView);
        if (webView.Parent is Panel parent)
        {
            parent.Children.Remove(webView);
        }

        if (webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.DownloadStarting -= OnDownloadStarting;
        }
        try { webView.Dispose(); } catch { }
    }

    private void SendInitialize()
    {
        Send(new
        {
            type = "initialize",
            paperId = _context.PaperId,
            providerId = _context.ProviderId,
            apiVersion = _context.ApiVersion,
            state = ParseState(_stateJson),
            stateVersion = _context.StateVersion,
            targetStateVersion = _context.TargetStateVersion,
            settings = ParseState(_settingsJson),
            theme = ThemePayload(_theme),
            visible = _runtimeVisible,
            presentationVisible = _presentationVisible
        });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !IsAllowedDocumentUri(e.Source))
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
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement
                : default;
            switch (type)
            {
                case "saveState":
                    _stateJson = payload.ValueKind == JsonValueKind.Undefined
                        ? "{}"
                        : payload.GetRawText();
                    _context.SaveStateJson(_stateJson);
                    break;
                case "setTitle":
                    _context.SetTitle(ReadPayloadString(payload));
                    break;
                case "setDisplayTitle":
                case "setCapsuleText":
                    _context.SetDisplayTitle(ReadPayloadString(payload));
                    break;
                case "setInputClaims":
                    _context.SetInputClaims(ReadInputClaims(payload));
                    break;
                case "markDirty":
                    _context.MarkDirty();
                    break;
                case "openExternal":
                    _context.OpenExternal(ReadPayloadString(payload));
                    break;
            }
        }
        catch
        {
            // A malformed plugin message is isolated to the plugin body.
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }

        ShowFailure(Strings.Format("PluginsWebProcessFailedFormat", e.ProcessFailedKind));
    }

    private static string ReadPayloadString(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.String
            ? payload.GetString() ?? ""
            : "";

    private static PaperBodyInputClaims ReadInputClaims(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
        {
            return PaperBodyInputClaims.None;
        }

        var claims = PaperBodyInputClaims.None;
        foreach (var item in payload.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            claims |= item.GetString() switch
            {
                "escapeKey" => PaperBodyInputClaims.EscapeKey,
                "contextMenu" => PaperBodyInputClaims.ContextMenu,
                _ => PaperBodyInputClaims.None
            };
        }
        return claims;
    }

    private static JsonElement ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static object ThemePayload(PaperBodyTheme theme) => new
    {
        isDark = theme.IsDark,
        paperColor = theme.PaperColor,
        textColor = theme.TextColor,
        weakTextColor = theme.WeakTextColor,
        accentColor = theme.AccentColor,
        borderColor = theme.BorderColor,
        fontFamily = theme.FontFamily,
        fontScale = theme.FontScale
    };

    private void Send(object value)
    {
        if (!_initialized ||
            !_documentReady ||
            !_pluginDocumentReady ||
            _disposed ||
            _webView.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value));
        }
        catch
        {
            // Renderer teardown can race with paper close.
        }
    }

    private void ShowFailure(string message)
    {
        if (_disposed)
        {
            return;
        }

        _documentReady = false;
        _pluginDocumentReady = false;
        _webViewFailed = true;
        UpdateWebViewPresentation();
        _context.SetDisplayTitle("");
        _context.SetInputClaims(PaperBodyInputClaims.None);
        for (var index = _root.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_root.Children[index], _webView))
            {
                _root.Children.RemoveAt(index);
            }
        }
        _root.Children.Insert(0, BuildStatusView(
            Strings.Format("PluginBodyFailureMessageFormat", _manifest.Name, message),
            isError: true,
            retry: _context.RequestReload));
    }

    private static FrameworkElement BuildStatusView(
        string text,
        bool isError = false,
        Action? retry = null)
    {
        var layout = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420
        };
        layout.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = isError ? Theme.DangerBrush : Theme.WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        if (retry != null)
        {
            var button = new Button
            {
                Content = Strings.Get("PluginBodyRetry"),
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 76,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Theme.Tint(28),
                Foreground = Theme.TextBrush,
                BorderBrush = Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(12)
            };
            button.Click += (_, _) => retry();
            layout.Children.Add(button);
        }

        return new Border
        {
            Padding = new Thickness(18),
            Background = Brushes.Transparent,
            Child = layout
        };
    }

    private static async Task<CoreWebView2Environment> GetPluginEnvironmentAsync(
        string pluginDirectory)
    {
        var key = Path.GetFullPath(pluginDirectory);
        Task<CoreWebView2Environment> task;
        lock (EnvironmentGate)
        {
            if (!EnvironmentTasks.TryGetValue(key, out task!))
            {
                task = CreateEnvironmentAsync(key);
                EnvironmentTasks.Add(key, task);
            }
        }

        try
        {
            return await task;
        }
        catch
        {
            lock (EnvironmentGate)
            {
                if (EnvironmentTasks.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, task))
                {
                    EnvironmentTasks.Remove(key);
                }
            }
            throw;
        }
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync(string pluginDirectory)
    {
        var userDataFolder = Path.Combine(
            pluginDirectory,
            ".runtime",
            "webview2");
        Directory.CreateDirectory(userDataFolder);
        var options = new CoreWebView2EnvironmentOptions(
            "--disable-background-timer-throttling " +
            "--disable-renderer-backgrounding " +
            "--disable-backgrounding-occluded-windows");
        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: options);
    }

    private static string WebHostName(string id)
    {
        var safe = new string(id
            .ToLowerInvariant()
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'
                    ? character
                    : '-')
            .ToArray())
            .Trim('-');
        if (safe.Length == 0)
        {
            safe = "plugin";
        }
        return $"{safe}.papertodo.local";
    }

    public void RefreshFromModel()
    {
        Send(new
        {
            type = "stateChanged",
            state = ParseState(_stateJson),
            stateVersion = _context.TargetStateVersion
        });
    }

    public void OnActivated() => Send(new { type = "activated" });
    public void OnDeactivated() => Send(new { type = "deactivated" });

    public void OnVisibilityChanged(bool visible)
    {
        _runtimeVisible = visible;
        UpdateWebViewHost();
        Send(new { type = "visibilityChanged", visible });
    }

    public void OnPresentationChanged(bool visible)
    {
        _presentationVisible = visible;
        UpdateWebViewHost();
        Send(new { type = "presentationChanged", visible });
    }

    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        Send(new { type = "themeChanged", theme = ThemePayload(theme) });
    }

    public void OnTypographyChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        Send(new { type = "typographyChanged", theme = ThemePayload(theme) });
    }

    public void OnSettingsChanged(string settingsJson)
    {
        _settingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        Send(new
        {
            type = "settingsChanged",
            settings = ParseState(_settingsJson)
        });
    }

    public void Commit()
    {
        // Web state persistence is immediate by contract. This message only asks a registered
        // state provider to flush a final snapshot while the renderer is still alive.
        Send(new { type = "commitRequested" });
    }

    public void CancelInteractions() => Send(new { type = "cancelInteractions" });
    public void OnDpiChanged() => Send(new { type = "dpiChanged" });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Commit();
        _disposed = true;
        _lifetime.Cancel();
        _webViewGeneration++;
        DisposeWebView(_webView);
        _lifetime.Dispose();
    }
}
