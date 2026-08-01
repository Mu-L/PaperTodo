using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private IPaperBodySession? _bodySession;
    private PaperBodyPluginDescriptor? _bodyDescriptor;
    private UIElement? _bodyElement;
    private FrameworkElement? _pluginBodyClipHost;
    private string _pluginDisplayTitle = "";
    private PaperBodyInputClaims _bodyInputClaims;
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

    internal bool TryGetPluginDisplayTitle(out string title)
    {
        title = _pluginDisplayTitle;
        return !IsCurrentBodyProviderMarkdown &&
            !_bodyFailed &&
            !string.IsNullOrWhiteSpace(title);
    }

    private bool BodyClaimsInput(PaperBodyInputClaims claim) =>
        !IsCurrentBodyProviderMarkdown &&
        (_bodyInputClaims & claim) == claim;

    private bool BodyRequires(PaperBodyRuntimeRequirements requirement) =>
        _bodyDescriptor != null &&
        (_bodyDescriptor.RuntimeRequirements & requirement) == requirement;

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
            return WrapPluginBodyView(view);
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
            return WrapPluginBodyView(session.View);
        }
    }

    private UIElement WrapPluginBodyView(FrameworkElement view)
    {
        if (IsCurrentBodyProviderMarkdown)
        {
            _pluginBodyClipHost = null;
            return view;
        }

        var host = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        host.Children.Add(view);
        host.SizeChanged += (_, _) => RefreshPluginBodyClip();
        _pluginBodyClipHost = host;
        RefreshPluginBodyClip();
        return host;
    }

    private void OnPaperChromeContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        var host = _pluginBodyClipHost;
        if (host == null ||
            !BodyClaimsInput(PaperBodyInputClaims.ContextMenu))
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var insidePluginBody = source != null && IsDescendantOf(source, host);
        if (!insidePluginBody)
        {
            var pointer = Mouse.GetPosition(host);
            insidePluginBody =
                pointer.X >= 0 &&
                pointer.Y >= 0 &&
                pointer.X < host.ActualWidth &&
                pointer.Y < host.ActualHeight;
            if (insidePluginBody)
            {
                source = host.InputHitTest(pointer) as DependencyObject ?? source;
            }
        }
        if (!insidePluginBody)
        {
            return;
        }

        var current = source;
        while (current != null &&
               !ReferenceEquals(current, host))
        {
            var menu = ContextMenuService.GetContextMenu(current);
            if (menu != null &&
                !ReferenceEquals(menu, _paperChrome.ContextMenu))
            {
                // A native plugin supplied its own menu, directly or through a style.
                return;
            }

            current = GetSafeParent(current);
        }

        // Suppress only the PaperTodo menu inherited from the paper chrome. The original
        // right-click remains unhandled and continues to the plugin/WebView.
        e.Handled = true;
    }

    private void RefreshPluginBodyClip()
    {
        var host = _pluginBodyClipHost;
        if (host == null)
        {
            return;
        }

        var width = host.ActualWidth;
        var height = host.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            host.Clip = null;
            return;
        }

        var chromeRadius = _paperChrome?.CornerRadius.BottomLeft ?? ExpandedChromeCornerRadius;
        var radius = Math.Min(
            Math.Max(0, chromeRadius - 1),
            Math.Min(width, height) / 2);
        if (radius <= 0)
        {
            host.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
            return;
        }

        var figure = new PathFigure
        {
            StartPoint = new Point(0, 0),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment(new Point(width, 0), true));
        figure.Segments.Add(new LineSegment(new Point(width, height - radius), true));
        figure.Segments.Add(new ArcSegment(
            new Point(width - radius, height),
            new Size(radius, radius),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(new Point(radius, height), true));
        figure.Segments.Add(new ArcSegment(
            new Point(0, height - radius),
            new Size(radius, radius),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        host.Clip = new PathGeometry([figure]);
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
            ApiVersion = descriptor.ApiVersion,
            StateJson = storedState.Json ?? "{}",
            StateVersion = storedState.Version,
            TargetStateVersion = descriptor.StateVersion,
            SettingsJson = _controller.PaperBodyPlugins.DataStore.GetSettingsJson(descriptor),
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
            SetDisplayTitle = text => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginDisplayTitle(text)),
            SetInputClaims = claims => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginInputClaims(claims),
                System.Windows.Threading.DispatcherPriority.Input),
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
        Action callback,
        System.Windows.Threading.DispatcherPriority priority =
            System.Windows.Threading.DispatcherPriority.Background)
    {
        void Invoke()
        {
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                _bodyFailed ||
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
        }), priority);
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

        ResetPluginRuntimeState(
            refreshTitle: _windowLifecycle == PaperWindowLifecycleState.Alive);
    }

    private PaperBodyStoredState ReadPluginState(string providerId) =>
        _controller.PaperBodyPlugins.DataStore.ReadPaperState(providerId, _paper.Id);

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

    private static string NormalizePluginStateJson(string? json) =>
        PaperBodyPluginDataStore.NormalizeStateJson(json);

    private void SavePluginStateValidated(
        string providerId,
        int stateVersion,
        string normalized)
    {
        _controller.PaperBodyPlugins.DataStore.SavePaperState(
            providerId,
            _paper.Id,
            stateVersion,
            normalized);
    }

    private void SetPluginDisplayTitle(string? text)
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
        if (string.Equals(_pluginDisplayTitle, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _pluginDisplayTitle = normalized;
        RefreshPaperTitle();
    }

    private void SetPluginInputClaims(PaperBodyInputClaims claims)
    {
        const PaperBodyInputClaims supportedClaims =
            PaperBodyInputClaims.EscapeKey |
            PaperBodyInputClaims.ContextMenu;
        _bodyInputClaims = claims & supportedClaims;
    }

    private void ResetPluginRuntimeState(bool refreshTitle)
    {
        var hadDisplayTitle = !string.IsNullOrEmpty(_pluginDisplayTitle);
        _pluginDisplayTitle = "";
        _bodyInputClaims = PaperBodyInputClaims.None;
        if (refreshTitle && hadDisplayTitle && _isShellBuilt)
        {
            RefreshPaperTitle();
        }
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
        NotifyCurrentPaperBodyVisibility(
            _paper.IsVisible && !_paper.IsCollapsed && WindowState != WindowState.Minimized);
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
        _pluginBodyClipHost = null;
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
        _bodyElement = WrapPluginBodyView(_bodySession.View);
        Grid.SetRow(_bodyElement, 1);
        Panel.SetZIndex(_bodyElement, 1);
        _shell.Children.Add(_bodyElement);
        RefreshPaperBodyChrome();
    }

    private void ClearPluginCapsuleTextOnFailure()
    {
        _paper.BodyCapsuleText = "";
        RefreshCapsuleLabel();
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
        if (IsCurrentBodyProviderMarkdown)
        {
            InvokeBodySession(item => item.OnVisibilityChanged(visible));
            return;
        }

        var runtimeVisible = _paper.IsVisible &&
            (visible ||
             BodyRequires(PaperBodyRuntimeRequirements.BackgroundUpdates));
        InvokeBodySession(item =>
        {
            // Presentation first avoids briefly starting a cold background controller when a
            // folded paper is being expanded in the same dispatcher turn.
            item.OnPresentationChanged(visible);
            item.OnVisibilityChanged(runtimeVisible);
        });
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

    internal void NotifyPaperBodyPluginSettingsChanged(
        string providerId,
        string settingsJson)
    {
        if (_paper.Type != PaperTypes.Note ||
            IsClosed ||
            IsCurrentBodyProviderMarkdown ||
            !string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        InvokeBodySession(item => item.OnSettingsChanged(settingsJson));
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
        _pluginBodyClipHost = null;
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

    private void ClearPluginRuntimeStateOnFailure()
    {
        ResetPluginRuntimeState(refreshTitle: true);
    }

    private sealed class FailedPaperBodySession : IPaperBodySession
    {
        private readonly PaperWindow _owner;
        public FailedPaperBodySession(PaperWindow owner, string pluginName, string message)
        {
            _owner = owner;
            owner.ClearPluginRuntimeStateOnFailure();
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
