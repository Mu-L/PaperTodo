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

// Web plugins are trusted and may use the network. The navigation, frame, popup, download and
// permission handlers below are light misuse guards, not a sandbox or a security boundary.
internal sealed class WebPaperBodySession : IPaperBodySession
{
    private static readonly object EnvironmentGate = new();
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly PaperBodyContext _context;
    private readonly PaperBodyPluginManifest _manifest;
    private readonly Grid _root;
    private readonly WebView2CompositionControl _webView;
    private readonly CancellationTokenSource _lifetime = new();
    private PaperBodyTheme _theme;
    private string _stateJson;
    private string _expectedOrigin = "";
    private Uri? _entryUri;
    private bool _initializationStarted;
    private bool _initialized;
    private bool _documentReady;
    private bool _disposed;
    private bool _visible = true;

    public WebPaperBodySession(
        PaperBodyContext context,
        PaperBodyPluginManifest manifest)
    {
        _context = context;
        _manifest = manifest;
        _theme = context.Theme;
        _stateJson = context.StateJson;
        _root = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _webView = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _webView.SetValue(UIElement.OpacityProperty, 0.0);
        _root.Children.Add(BuildStatusView(Strings.Get("PluginsWebLoading")));
        _root.Children.Add(_webView);
        _root.Loaded += OnRootLoaded;
        _root.SizeChanged += OnRootSizeChanged;
    }

    public FrameworkElement View => _root;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= OnRootLoaded;
        TryStartInitialization();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) =>
        TryStartInitialization();

    private void TryStartInitialization()
    {
        if (_initializationStarted ||
            !_visible ||
            _disposed ||
            !_root.IsLoaded ||
            _root.ActualWidth <= 0 ||
            _root.ActualHeight <= 0)
        {
            return;
        }
        _initializationStarted = true;
        _root.SizeChanged -= OnRootSizeChanged;
        _ = InitializeAsync(_lifetime.Token);
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            var environment = await GetPluginEnvironmentAsync(_manifest.DirectoryPath);
            token.ThrowIfCancellationRequested();
            await _webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();

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
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildBridgeScript(_expectedOrigin));
            core.SetVirtualHostNameToFolderMapping(
                hostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Set readiness before navigation. A tiny local document can complete synchronously
            // enough for NavigationCompleted to run before the line after Source assignment.
            _initialized = true;
            _documentReady = false;
            _webView.Source = _entryUri;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _initializationStarted = false;
            ShowFailure(ex.GetBaseException().Message);
        }
    }

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
                setCapsuleText(text) { post('setCapsuleText', String(text ?? '')); },
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
        _documentReady = false;
        if (IsAllowedDocumentUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenExternalNavigation(e.Uri);
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedDocumentUri(e.Uri) ||
            string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        e.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _webView.Source == null || !IsAllowedDocumentUri(_webView.Source.AbsoluteUri))
        {
            ShowFailure(
                $"{Strings.Get("PluginsWebNavigationFailed")} ({e.WebErrorStatus})");
            return;
        }

        _documentReady = true;
        ShowWebView();
        SendInitialize();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalNavigation(e.Uri);
    }

    private static void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
    }

    private static void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    private bool IsAllowedDocumentUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), _expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenExternalNavigation(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "mailto")
        {
            _context.OpenExternal(uri.AbsoluteUri);
        }
    }

    private void ShowWebView()
    {
        for (var index = _root.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_root.Children[index], _webView))
            {
                _root.Children.RemoveAt(index);
            }
        }
        UpdateWebViewPresentation();
    }

    private void UpdateWebViewPresentation()
    {
        var show = _visible && _documentReady && !_disposed;
        _webView.SetValue(UIElement.OpacityProperty, show ? 1.0 : 0.0);
        _webView.IsHitTestVisible = show;
    }

    private void SendInitialize()
    {
        Send(new
        {
            type = "initialize",
            paperId = _context.PaperId,
            providerId = _context.ProviderId,
            state = ParseState(_stateJson),
            stateVersion = _context.StateVersion,
            targetStateVersion = _context.TargetStateVersion,
            theme = ThemePayload(_theme),
            visible = _visible
        });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAllowedDocumentUri(e.Source))
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
                case "setCapsuleText":
                    _context.SetCapsuleText(ReadPayloadString(payload));
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
        ShowFailure(Strings.Format("PluginsWebProcessFailedFormat", e.ProcessFailedKind));
    }

    private static string ReadPayloadString(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.String
            ? payload.GetString() ?? ""
            : "";

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
        if (!_initialized || !_documentReady || _disposed || _webView.CoreWebView2 == null)
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
        UpdateWebViewPresentation();
        _context.SetCapsuleText("");
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
        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);
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
        _visible = visible;
        if (visible)
        {
            TryStartInitialization();
        }
        UpdateWebViewPresentation();
        Send(new { type = "visibilityChanged", visible });
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
        _root.Loaded -= OnRootLoaded;
        _root.SizeChanged -= OnRootSizeChanged;
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.NavigationStarting -= OnNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
            core.DownloadStarting -= OnDownloadStarting;
        }
        try { _webView.Dispose(); } catch { }
        _lifetime.Dispose();
    }
}
