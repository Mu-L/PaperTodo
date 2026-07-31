using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const int MaximumPluginStateCharacters = 1_000_000;
    private IPaperBodySession? _bodySession;
    private PaperBodyPluginDescriptor? _bodyDescriptor;
    private UIElement? _bodyElement;
    private MarkdownPaperBodySession? _markdownBodySession;
    private int _bodySessionGeneration;
    private bool _bodyFailed;
    private readonly object _pendingPluginStateGate = new();
    private readonly Dictionary<(int Generation, string ProviderId), PendingPluginState>
        _pendingPluginStates = new();

    private sealed record PendingPluginState(int Version, string Json);

    // Staged Markdown extraction: mutable editor state lives in MarkdownPaperBodySession while
    // the mature interaction methods remain in PaperWindow.Note.cs for this release.
    private MarkdownTextBox? _noteBox
    {
        get => _markdownBodySession?.NoteBox;
        set => RequireMarkdownBodySession().NoteBox = value;
    }
    private UIElement? _noteBodyElement
    {
        get => _markdownBodySession?.CurrentPresenter;
        set => RequireMarkdownBodySession().CurrentPresenter = value;
    }
    private Action? _showNotePreview
    {
        get => _markdownBodySession?.ShowPreview;
        set => RequireMarkdownBodySession().ShowPreview = value;
    }
    private int _notePresenterGeneration
    {
        get => _markdownBodySession?.PresenterGeneration ?? 0;
        set => RequireMarkdownBodySession().PresenterGeneration = value;
    }
    private int _noteDeferredWorkGeneration
    {
        get => _markdownBodySession?.DeferredWorkGeneration ?? 0;
        set => RequireMarkdownBodySession().DeferredWorkGeneration = value;
    }
    private Action? _cancelNotePresenterInteractions
    {
        get => _markdownBodySession?.CancelPresenterInteractions;
        set => RequireMarkdownBodySession().CancelPresenterInteractions = value;
    }
    private Action? _settlePendingNoteBodyRebuild
    {
        get => _markdownBodySession?.SettlePendingBodyRebuild;
        set => RequireMarkdownBodySession().SettlePendingBodyRebuild = value;
    }
    private bool _noteContentDirty
    {
        get => _markdownBodySession?.ContentDirty == true;
        set => RequireMarkdownBodySession().ContentDirty = value;
    }
    private bool _applyingExternalNoteChange
    {
        get => _markdownBodySession?.ApplyingExternalChange == true;
        set => RequireMarkdownBodySession().ApplyingExternalChange = value;
    }
    private bool _liveIsScriptCapsule
    {
        get => _markdownBodySession?.LiveIsScriptCapsule == true;
        set => RequireMarkdownBodySession().LiveIsScriptCapsule = value;
    }

    private MarkdownPaperBodySession RequireMarkdownBodySession() =>
        _markdownBodySession ?? throw new InvalidOperationException(
            "Markdown presenter state is unavailable outside a Markdown body session.");

    internal void AttachMarkdownBodySession(MarkdownPaperBodySession session)
    {
        if (_markdownBodySession != null && !ReferenceEquals(_markdownBodySession, session))
        {
            throw new InvalidOperationException("A Markdown body session is already attached.");
        }
        _markdownBodySession = session;
    }

    internal void DetachMarkdownBodySession(MarkdownPaperBodySession session)
    {
        if (ReferenceEquals(_markdownBodySession, session))
        {
            _markdownBodySession = null;
        }
    }

    internal bool IsCurrentBodyProviderMarkdown =>
        _paper.Type == PaperTypes.Note &&
        string.Equals(
            NormalizeBodyProviderId(_paper.BodyProviderId),
            PaperBodyProviderIds.Markdown,
            StringComparison.Ordinal);

    private PaperBodyCapabilities CurrentBodyCapabilities
    {
        get
        {
            if (_paper.Type != PaperTypes.Note || _bodyFailed)
            {
                return PaperBodyCapabilities.None;
            }
            if (_bodyDescriptor != null)
            {
                return _bodyDescriptor.Capabilities;
            }
            return _controller.PaperBodyPlugins.TryGet(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    out var descriptor)
                ? descriptor.Capabilities
                : PaperBodyCapabilities.None;
        }
    }

    private bool BodySupports(PaperBodyCapabilities capability) =>
        (CurrentBodyCapabilities & capability) == capability;

    private UIElement CreateAndAttachInitialPaperBody()
    {
        _bodyFailed = false;
        var generation = NextBodySessionGeneration();
        var body = CreatePaperBodyView(generation, out var session);
        _bodySession = session;
        _bodyElement = body;
        return body;
    }

    private int NextBodySessionGeneration()
    {
        lock (_pendingPluginStateGate)
        {
            return ++_bodySessionGeneration;
        }
    }

    private IPaperBodySession CreatePaperBodySession(int generation)
    {
        var providerId = NormalizeBodyProviderId(_paper.BodyProviderId);
        _paper.BodyProviderId = providerId;
        if (string.Equals(providerId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal))
        {
            _controller.PaperBodyPlugins.TryGet(providerId, out var markdownDescriptor);
            _bodyDescriptor = markdownDescriptor;
            return new MarkdownPaperBodySession(this);
        }

        if (!_controller.PaperBodyPlugins.TryGet(providerId, out var descriptor))
        {
            _bodyDescriptor = null;
            _bodyFailed = true;
            return new FailedPaperBodySession(
                this,
                providerId,
                Strings.Format("PluginsMissingProviderFormat", providerId));
        }

        _bodyDescriptor = descriptor;
        try
        {
            if (descriptor.Kind == PaperBodyPluginKind.Native)
            {
                var stored = ReadPluginState(descriptor.Id);
                var activation =
                    _controller.PaperBodyPlugins.CreateNativePlugin(descriptor);
                var plugin = activation.Plugin;
                descriptor = activation.Descriptor;
                _bodyDescriptor = descriptor;
                IPaperBodySession? createdSession = null;
                try
                {
                    var migrated = MigrateNativePluginState(plugin, descriptor, stored);
                    var context = CreatePluginContext(descriptor, generation, migrated);
                    createdSession = plugin.Create(context)
                        ?? throw new InvalidOperationException("Plugin returned a null body session.");
                    if (migrated.Version != stored.Version)
                    {
                        SavePluginStateValidated(
                            descriptor.Id,
                            migrated.Version,
                            migrated.Json);
                    }
                    return createdSession;
                }
                finally
                {
                    if (!ReferenceEquals(plugin, createdSession) &&
                        plugin is IDisposable disposable)
                    {
                        try { disposable.Dispose(); } catch { }
                    }
                }
            }

            if (descriptor.Kind == PaperBodyPluginKind.Web && descriptor.Manifest != null)
            {
                var stored = ReadPluginState(descriptor.Id);
                if (stored.Version > descriptor.StateVersion)
                {
                    throw new InvalidOperationException(
                        $"Saved plugin state version {stored.Version} is newer than supported version {descriptor.StateVersion}.");
                }
                var context = CreatePluginContext(descriptor, generation, stored);
                return new WebPaperBodySession(context, descriptor.Manifest);
            }

            throw new InvalidOperationException("Plugin descriptor has no usable body factory.");
        }
        catch (Exception ex)
        {
            _bodyDescriptor = null;
            _bodyFailed = true;
            return new FailedPaperBodySession(
                this,
                descriptor.DisplayName,
                ex.GetBaseException().Message);
        }
    }

    private UIElement CreatePaperBodyView(
        int generation,
        out IPaperBodySession session)
    {
        session = CreatePaperBodySession(generation);
        try
        {
            var view = session.View
                ?? throw new InvalidOperationException("Plugin returned a body session with no view.");
            if (view is Window || view.Parent != null)
            {
                throw new InvalidOperationException(
                    "Plugin body View must be an unparented FrameworkElement, not a Window or a reused control.");
            }
            _bodyFailed = session is FailedPaperBodySession;
            return view;
        }
        catch (Exception ex)
        {
            try { session.Dispose(); } catch { }
            var pluginName = _bodyDescriptor?.DisplayName ?? _paper.BodyProviderId;
            _bodyDescriptor = null;
            _bodyFailed = true;
            session = new FailedPaperBodySession(
                this,
                pluginName,
                ex.GetBaseException().Message);
            return session.View;
        }
    }

    private PaperBodyContext CreatePluginContext(
        PaperBodyPluginDescriptor descriptor,
        int generation,
        PaperBodyStoredState storedState)
    {
        var providerId = descriptor.Id;
        return new PaperBodyContext
        {
            PaperId = _paper.Id,
            ProviderId = providerId,
            StateJson = storedState.Json ?? "{}",
            StateVersion = storedState.Version,
            TargetStateVersion = descriptor.StateVersion,
            Theme = CurrentPaperBodyTheme(),
            SaveStateJson = json => QueuePluginStateSave(
                generation,
                providerId,
                descriptor.StateVersion,
                json),
            SetTitle = title => InvokePluginContext(
                generation,
                providerId,
                () => _controller.UpdatePaperTitle(_paper, title)),
            SetCapsuleText = text => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginCapsuleText(text)),
            MarkDirty = () => InvokePluginContext(
                generation,
                providerId,
                _controller.MarkDirty),
            OpenExternal = value => InvokePluginContext(
                generation,
                providerId,
                () => OpenPluginExternal(value)),
            RequestReload = () => InvokePluginContext(
                generation,
                providerId,
                ReloadCurrentPaperBody)
        };
    }

    private void InvokePluginContext(
        int generation,
        string providerId,
        Action callback)
    {
        void Invoke()
        {
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                generation != _bodySessionGeneration ||
                !string.Equals(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal))
            {
                return;
            }
            callback();
        }

        // Always queue callbacks, even when a plugin calls during Create. This prevents a
        // RequestReload or title/state callback from re-entering body construction.
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            try
            {
                Invoke();
            }
            catch (Exception ex)
            {
                if (_windowLifecycle == PaperWindowLifecycleState.Alive)
                {
                    ReplaceBodyWithFailure(ex.GetBaseException().Message);
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void QueuePluginStateSave(
        int generation,
        string providerId,
        int stateVersion,
        string? json)
    {
        var normalized = NormalizePluginStateJson(json);
        lock (_pendingPluginStateGate)
        {
            if (generation != _bodySessionGeneration ||
                !string.Equals(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _pendingPluginStates[(generation, providerId)] =
                new PendingPluginState(Math.Max(1, stateVersion), normalized);
        }

        if (Dispatcher.CheckAccess())
        {
            FlushPendingPluginState(generation, providerId);
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            try
            {
                FlushPendingPluginState(generation, providerId);
            }
            catch (Exception ex)
            {
                if (!IsClosed && generation == _bodySessionGeneration)
                {
                    ReplaceBodyWithFailure(ex.GetBaseException().Message);
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushPendingPluginState(int generation, string providerId)
    {
        PendingPluginState? pending;
        lock (_pendingPluginStateGate)
        {
            if (generation != _bodySessionGeneration ||
                !string.Equals(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal) ||
                !_pendingPluginStates.Remove((generation, providerId), out pending))
            {
                return;
            }
        }

        SavePluginStateValidated(providerId, pending.Version, pending.Json);
    }

    private PendingPluginState? InvalidateBodySessionAndTakePending(
        int generation,
        string providerId)
    {
        lock (_pendingPluginStateGate)
        {
            _pendingPluginStates.Remove((generation, providerId), out var pending);
            _bodySessionGeneration++;
            foreach (var key in _pendingPluginStates.Keys
                         .Where(key => key.Generation <= generation)
                         .ToArray())
            {
                _pendingPluginStates.Remove(key);
            }
            return pending;
        }
    }

    private void CommitDisposeAndInvalidateCurrentBody(bool cancelInteractions)
    {
        var session = _bodySession;
        var generation = _bodySessionGeneration;
        var providerId = NormalizeBodyProviderId(_paper.BodyProviderId);
        if (session != null)
        {
            try { session.Commit(); } catch { }
            if (cancelInteractions)
            {
                try { session.CancelInteractions(); } catch { }
            }
            try { session.Dispose(); } catch { }
        }

        var pending = InvalidateBodySessionAndTakePending(generation, providerId);
        if (pending != null)
        {
            SavePluginStateValidated(providerId, pending.Version, pending.Json);
        }
    }

    private PaperBodyStoredState ReadPluginState(string providerId)
    {
        _paper.BodyStates ??= new Dictionary<string, PaperBodyStoredState>(StringComparer.Ordinal);
        if (!_paper.BodyStates.TryGetValue(providerId, out var state))
        {
            return new PaperBodyStoredState();
        }
        if (state == null)
        {
            throw new InvalidOperationException(
                $"Saved state for plugin '{providerId}' is null.");
        }
        if (state.Version < 1)
        {
            throw new InvalidOperationException(
                $"Saved state version {state.Version} for plugin '{providerId}' is invalid.");
        }

        return new PaperBodyStoredState
        {
            Version = state.Version,
            Json = ValidateStoredPluginStateJson(providerId, state.Json)
        };
    }

    private static string ValidateStoredPluginStateJson(
        string providerId,
        string? json)
    {
        if (json == null)
        {
            throw new InvalidOperationException(
                $"Saved state for plugin '{providerId}' has no JSON payload.");
        }
        if (json.Length > MaximumPluginStateCharacters)
        {
            throw new InvalidOperationException(
                $"Saved state for plugin '{providerId}' exceeds {MaximumPluginStateCharacters} characters.");
        }

        try
        {
            using (JsonDocument.Parse(json))
            {
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Saved state for plugin '{providerId}' is not valid JSON.",
                ex);
        }
        return json;
    }

    private PaperBodyStoredState MigrateNativePluginState(
        IPaperBodyPlugin plugin,
        PaperBodyPluginDescriptor descriptor,
        PaperBodyStoredState stored)
    {
        if (stored.Version > descriptor.StateVersion)
        {
            throw new InvalidOperationException(
                $"Saved plugin state version {stored.Version} is newer than supported version {descriptor.StateVersion}.");
        }
        if (stored.Version == descriptor.StateVersion)
        {
            return stored;
        }

        var migratedJson = NormalizePluginStateJson(
            plugin.MigrateState(stored.Json ?? "{}", stored.Version));
        return new PaperBodyStoredState
        {
            Version = descriptor.StateVersion,
            Json = migratedJson
        };
    }

    private static string NormalizePluginStateJson(string? json)
    {
        var normalized = string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
        if (normalized.Length > MaximumPluginStateCharacters)
        {
            throw new InvalidOperationException(
                $"Plugin state cannot exceed {MaximumPluginStateCharacters} characters.");
        }
        using (JsonDocument.Parse(normalized))
        {
        }
        return normalized;
    }

    private void SavePluginStateValidated(
        string providerId,
        int stateVersion,
        string normalized)
    {
        _paper.BodyStates ??= new Dictionary<string, PaperBodyStoredState>(StringComparer.Ordinal);
        stateVersion = Math.Max(1, stateVersion);
        if (_paper.BodyStates.TryGetValue(providerId, out var existing) &&
            existing != null &&
            existing.Version == stateVersion &&
            string.Equals(existing.Json, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _paper.BodyStates[providerId] = new PaperBodyStoredState
        {
            Version = stateVersion,
            Json = normalized
        };
        _controller.MarkDirty();
    }

    private void SetPluginCapsuleText(string? text)
    {
        var normalized = string.Join(
            " ",
            (text ?? "")
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        if (normalized.Length > 120)
        {
            normalized = normalized[..119] + "…";
        }
        if (string.Equals(_paper.BodyCapsuleText, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _paper.BodyCapsuleText = normalized;
        RefreshPaperTitle();
        _controller.MarkDirty();
    }

    private static void OpenPluginExternal(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening an external resource is optional plugin behavior.
        }
    }

    internal void SwitchPaperBodyProvider(string providerId)
    {
        if (_paper.Type != PaperTypes.Note || IsClosed)
        {
            return;
        }

        var normalized = NormalizeBodyProviderId(providerId);
        if (string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        CommitPendingEditsForSave();
        RemoveCurrentPaperBody();
        _paper.BodyProviderId = normalized;
        _paper.BodyCapsuleText = "";
        AttachCurrentPaperBody();
        RefreshPaperBodyChrome();
        RefreshPaperTitle();
        _controller.MarkDirty();
    }

    private void ReloadCurrentPaperBody()
    {
        if (_paper.Type != PaperTypes.Note || IsClosed)
        {
            return;
        }

        CommitPendingEditsForSave();
        RemoveCurrentPaperBody();
        AttachCurrentPaperBody();
        RefreshPaperBodyChrome();
        RefreshPaperTitle();
    }

    internal void RefreshPaperBodyProviderAvailability(IReadOnlySet<string> changedProviderIds)
    {
        if (_paper.Type != PaperTypes.Note || IsClosed)
        {
            return;
        }

        var currentId = NormalizeBodyProviderId(_paper.BodyProviderId);
        if (!IsCurrentBodyProviderMarkdown &&
            (changedProviderIds.Contains(currentId) ||
             !_controller.PaperBodyPlugins.TryGet(currentId, out _)))
        {
            ReloadCurrentPaperBody();
            return;
        }

        RefreshPaperContextMenus();
    }

    private void AttachCurrentPaperBody()
    {
        _bodyFailed = false;
        var generation = NextBodySessionGeneration();
        var body = CreatePaperBodyView(generation, out var session);
        _bodySession = session;
        _bodyElement = body;
        Grid.SetRow(body, 1);
        Panel.SetZIndex(body, 1);
        _shell.Children.Add(body);
        InvokeBodySession(item => item.OnVisibilityChanged(
            _paper.IsVisible && !_paper.IsCollapsed && WindowState != WindowState.Minimized));
    }

    private void RemoveCurrentPaperBody()
    {
        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);
        if (_bodyElement != null)
        {
            _shell.Children.Remove(_bodyElement);
        }
        _bodySession = null;
        _bodyDescriptor = null;
        _bodyElement = null;
        _bodyFailed = false;
        RemoveTextZoomOverlay();
    }

    private void RefreshPaperBodyChrome()
    {
        if (_linkNoteButton != null)
        {
            _linkNoteButton.Visibility =
                _controller.State.EnableTodoNoteLinks &&
                BodySupports(PaperBodyCapabilities.NoteLinks)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Visibility =
                _controller.State.ShowTopBarExternalOpenButton &&
                IsCurrentBodyProviderMarkdown
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        RemoveTextZoomOverlay();
        if (BodySupports(PaperBodyCapabilities.TextZoom))
        {
            BuildTextZoomOverlay();
            UpdateTextZoom();
        }
        RefreshPaperContextMenus();
    }

    private void RemoveTextZoomOverlay()
    {
        if (_textZoomIndicator?.Parent is UIElement host)
        {
            _shell.Children.Remove(host);
        }
        _textZoomIndicator = null;
    }

    private void InvokeBodySession(
        Action<IPaperBodySession> callback,
        bool disableOnFailure = true)
    {
        var session = _bodySession;
        if (session == null)
        {
            return;
        }

        try
        {
            callback(session);
        }
        catch (Exception ex)
        {
            if (!disableOnFailure ||
                _windowLifecycle != PaperWindowLifecycleState.Alive)
            {
                return;
            }
            ReplaceBodyWithFailure(ex.GetBaseException().Message);
        }
    }

    private void ReplaceBodyWithFailure(string message)
    {
        var providerName = _bodyDescriptor?.DisplayName ?? _paper.BodyProviderId;
        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);
        if (_bodyElement != null)
        {
            _shell.Children.Remove(_bodyElement);
        }
        ClearPluginCapsuleTextOnFailure();
        _bodySession = new FailedPaperBodySession(this, providerName, message);
        _bodyDescriptor = null;
        _bodyFailed = true;
        _bodyElement = _bodySession.View;
        Grid.SetRow(_bodyElement, 1);
        Panel.SetZIndex(_bodyElement, 1);
        _shell.Children.Add(_bodyElement);
        RefreshPaperBodyChrome();
    }

    internal void CommitCurrentPaperBody()
    {
        InvokeBodySession(item => item.Commit());
    }

    internal void CancelCurrentPaperBodyInteractions()
    {
        InvokeBodySession(item => item.CancelInteractions());
    }

    internal void NotifyCurrentPaperBodyVisibility(bool visible)
    {
        InvokeBodySession(item => item.OnVisibilityChanged(visible));
    }

    internal void NotifyCurrentPaperBodyActivated()
    {
        InvokeBodySession(item => item.OnActivated());
    }

    internal void NotifyCurrentPaperBodyDeactivated()
    {
        InvokeBodySession(item => item.OnDeactivated());
    }

    internal void NotifyCurrentPaperBodyThemeChanged()
    {
        InvokeBodySession(item => item.OnThemeChanged(CurrentPaperBodyTheme()));
    }

    internal void NotifyCurrentPaperBodyTypographyChanged()
    {
        InvokeBodySession(item => item.OnTypographyChanged(CurrentPaperBodyTheme()));
    }

    internal void NotifyCurrentPaperBodyDpiChanged()
    {
        InvokeBodySession(item => item.OnDpiChanged());
    }

    internal void RefreshCurrentPaperBodyFromModel()
    {
        InvokeBodySession(item => item.RefreshFromModel());
    }

    internal void DisposeCurrentPaperBody()
    {
        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);
        _bodySession = null;
        _bodyDescriptor = null;
        _bodyElement = null;
        _bodyFailed = false;
    }

    private PaperBodyTheme CurrentPaperBodyTheme()
    {
        return new PaperBodyTheme(
            Theme.IsDark,
            BrushHex(Theme.PaperBrush, "#FFF8E6"),
            BrushHex(Theme.TextBrush, "#202020"),
            BrushHex(Theme.WeakTextBrush, "#707070"),
            BrushHex(Theme.ActiveBrush, "#B07A31"),
            BrushHex(Theme.PaperBorderBrush, "#807050"),
            AppTypography.UiFontFamily.Source,
            AppTypography.ScaleFactor *
                (BodySupports(PaperBodyCapabilities.TextZoom)
                    ? CurrentTextZoom()
                    : 1.0));
    }

    private static string BrushHex(Brush brush, string fallback)
    {
        if (brush is not SolidColorBrush solid)
        {
            return fallback;
        }
        var color = solid.Color;
        // Use the same six-digit RGB form for CSS and WPF ColorConverter consumers.
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string NormalizeBodyProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId)
            ? PaperBodyProviderIds.Markdown
            : providerId.Trim();
    }

    internal MenuItem BuildPaperBodyProviderMenuItem()
    {
        var root = new MenuItem
        {
            Header = Strings.Get("PaperBodyMenu")
        };
        var currentId = NormalizeBodyProviderId(_paper.BodyProviderId);
        foreach (var descriptor in _controller.PaperBodyPlugins.Descriptors)
        {
            var item = new MenuItem
            {
                Header = descriptor.DisplayName,
                IsCheckable = true,
                IsChecked = string.Equals(currentId, descriptor.Id, StringComparison.Ordinal),
                StaysOpenOnClick = false,
                ToolTip = string.IsNullOrWhiteSpace(descriptor.Description)
                    ? null
                    : descriptor.Description
            };
            var providerId = descriptor.Id;
            item.Click += (_, _) => SwitchPaperBodyProvider(providerId);
            root.Items.Add(item);
        }

        if (!_controller.PaperBodyPlugins.TryGet(currentId, out _))
        {
            root.Items.Add(new Separator());
            root.Items.Add(new MenuItem
            {
                Header = Strings.Format("PluginsMissingProviderFormat", currentId),
                IsEnabled = false
            });
        }
        return root;
    }


    internal void CommitLegacyMarkdownContent()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null || !_noteContentDirty)
        {
            return;
        }

        _paper.Content = _noteBox.PersistentText;
        _noteContentDirty = false;
    }

    internal void RefreshLegacyMarkdownFromModel()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null)
        {
            return;
        }

        var content = _paper.Content ?? "";
        var caret = Math.Clamp(_noteBox.CaretIndex, 0, content.Length);
        _applyingExternalNoteChange = true;
        try
        {
            _noteBox.Text = content;
        }
        finally
        {
            _applyingExternalNoteChange = false;
        }
        _noteBox.CaretIndex = caret;
        _noteContentDirty = false;

        var wasScriptCapsule = _liveIsScriptCapsule;
        _liveIsScriptCapsule = IsScriptCapsuleDocument(_noteBox);
        if (wasScriptCapsule != _liveIsScriptCapsule)
        {
            RefreshCapsuleLabel();
            RefreshPaperContextMenus();
        }
    }

    internal FrameworkElement CreateMarkdownBodyView() =>
        (FrameworkElement)BuildNoteBody();

    internal void CancelLegacyMarkdownInteractions() =>
        CancelNotePresenterDeferredWork();

    internal void RefreshLegacyMarkdownTheme() =>
        _noteBox?.RefreshVisualStyle();

    internal void RefreshLegacyMarkdownTypography() =>
        _noteBox?.RefreshTypography();

    internal void RefreshLegacyMarkdownDpi() =>
        _noteBox?.RefreshImageDecodeForCurrentDpi();

    internal void SetLegacyMarkdownVisibility(bool visible)
    {
        _noteBox?.SetImageRenderingSuspended(!visible);
        if (!visible)
        {
            _controller.ImageStore.ReleaseNoteBitmapCache(_paper.Id);
        }
    }

    private void ClearPluginCapsuleTextOnFailure()
    {
        if (IsCurrentBodyProviderMarkdown || string.IsNullOrEmpty(_paper.BodyCapsuleText))
        {
            return;
        }
        _paper.BodyCapsuleText = "";
        if (_isShellBuilt)
        {
            RefreshPaperTitle();
        }
        _controller.MarkDirty();
    }

    private sealed class FailedPaperBodySession : IPaperBodySession
    {
        private readonly PaperWindow _owner;
        public FailedPaperBodySession(PaperWindow owner, string pluginName, string message)
        {
            _owner = owner;
            owner.ClearPluginCapsuleTextOnFailure();
            var layout = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 420
            };
            layout.Children.Add(new TextBlock
            {
                Text = Strings.Get("PluginBodyFailureTitle"),
                Foreground = Theme.TextBrush,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(14),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            layout.Children.Add(new TextBlock
            {
                Text = Strings.Format("PluginBodyFailureMessageFormat", pluginName, message),
                Foreground = Theme.WeakTextBrush,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(12),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 12)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var retry = CreateButton(Strings.Get("PluginBodyRetry"));
            retry.Click += (_, _) => owner.ReloadCurrentPaperBody();
            var markdown = CreateButton(Strings.Get("PluginBodyUseMarkdown"));
            markdown.Margin = new Thickness(8, 0, 0, 0);
            markdown.Click += (_, _) => owner.SwitchPaperBodyProvider(PaperBodyProviderIds.Markdown);
            buttons.Children.Add(retry);
            buttons.Children.Add(markdown);
            layout.Children.Add(buttons);

            View = new Border
            {
                Padding = new Thickness(20),
                Background = Brushes.Transparent,
                Child = layout
            };
        }

        public FrameworkElement View { get; }

        private static Button CreateButton(string text)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 76,
                Background = Theme.Tint(28),
                Foreground = Theme.TextBrush,
                BorderBrush = Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(12)
            };
        }

        public void Dispose() { }
    }
}
