using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string PluginAppRuntimeCapability = "appRuntime";
    private static readonly TimeSpan[] PluginAppRuntimeRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10)
    ];

    private sealed class PluginAppRuntimeLifetime
    {
        public bool Active { get; set; } = true;
    }

    private sealed class PluginAppRuntimeOwnershipCanceledException(string message)
        : Exception(message);

    private sealed class PluginAppRuntimeLease : IDisposable
    {
        public required Guid RuntimeId { get; init; }
        public required string ProviderId { get; init; }
        public required PluginAppRuntimeLifetime Lifetime { get; init; }
        public required PaperAppRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperAppRuntimeGlobalTopBarApi GlobalTopBar { get; init; }
        public required PaperAppRuntimeGlobalShortcutApi GlobalShortcuts { get; init; }
        public IDisposable? Runtime { get; init; }
        public IPaperBodyPlugin? NativeFactory { get; init; }

        public void Dispose()
        {
            if (!Lifetime.Active)
            {
                return;
            }
            Lifetime.Active = false;
            try { Runtime?.Dispose(); } catch { }
            try { GlobalShortcuts.Dispose(); } catch { }
            try { GlobalTopBar.Dispose(); } catch { }
            try { Workspace.Dispose(); } catch { }
            if (NativeFactory is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }

    private readonly Dictionary<string, PluginAppRuntimeLease> _pluginAppRuntimes =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _pluginAppRuntimeStarts =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _pluginAppRuntimeStartFailures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pluginAppRuntimeStartFailureCounts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pluginAppRuntimeRetryTokens =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _pluginAppRuntimeRestartRequests =
        new(StringComparer.Ordinal);
    private int _pluginAppRuntimeNextRetryToken;
    private bool _pluginAppRuntimeReconciliationEnabled;
    private bool _pluginAppRuntimeDisposing;

    /// <summary>
    /// Enables process-level plugin runtimes only after startupPaper handling has settled. From this
    /// point on, the final State.Papers provider set is the authority: 0 -> 1 entity paper starts the
    /// provider runtime and 1 -> 0 stops it. Paper visibility/presentation/body-session state is not
    /// part of this lifetime decision.
    /// </summary>
    internal void EnablePluginAppRuntimeReconciliation()
    {
        if (_pluginAppRuntimeDisposing || IsExiting)
        {
            return;
        }

        // Host-owned paper.* shortcuts do not require a live appRuntime. Register them when plugin
        // startup ownership becomes authoritative; custom actions are activated later by their
        // provider runtime's GlobalShortcuts handler.
        RefreshPluginShortcuts();
        _pluginAppRuntimeReconciliationEnabled = true;
        ReconcilePluginAppRuntimes();
    }

    internal void ReconcilePluginAppRuntimes()
    {
        if (!_pluginAppRuntimeReconciliationEnabled ||
            _pluginAppRuntimeDisposing ||
            IsExiting)
        {
            return;
        }

        var desired = PaperBodyPlugins.Descriptors
            .Where(DeclaresPluginAppRuntime)
            .Where(descriptor => HasEntityPluginPaper(descriptor.Id))
            .ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);
        var runtimeSetChanged = false;

        foreach (var providerId in _pluginAppRuntimes.Keys
                     .Where(providerId => !desired.ContainsKey(providerId))
                     .ToArray())
        {
            var lease = _pluginAppRuntimes[providerId];
            _pluginAppRuntimes.Remove(providerId);
            lease.Dispose();
            ClearPluginAppRuntimeFailureState(providerId);
            _pluginAppRuntimeRestartRequests.Remove(providerId);
            runtimeSetChanged = true;
        }

        foreach (var providerId in _pluginAppRuntimeStartFailures
                     .Where(providerId => !desired.ContainsKey(providerId))
                     .ToArray())
        {
            ClearPluginAppRuntimeFailureState(providerId);
            _pluginAppRuntimeRestartRequests.Remove(providerId);
            runtimeSetChanged = true;
        }

        foreach (var descriptor in desired.Values)
        {
            if (_pluginAppRuntimes.ContainsKey(descriptor.Id) ||
                _pluginAppRuntimeStarts.Contains(descriptor.Id) ||
                _pluginAppRuntimeStartFailures.Contains(descriptor.Id))
            {
                continue;
            }
            _pluginAppRuntimeStarts.Add(descriptor.Id);
            _ = StartPluginAppRuntimeSafelyAsync(descriptor);
        }

        if (runtimeSetChanged)
        {
            QueuePluginStatusUiRefresh();
        }
    }

    private bool HasEntityPluginPaper(string providerId) =>
        State.Papers.Any(paper =>
            paper.Type == PaperTypes.Note &&
            string.Equals(
                paper.BodyProviderId?.Trim(),
                providerId,
                StringComparison.Ordinal));

    private async Task StartPluginAppRuntimeSafelyAsync(
        PaperBodyPluginDescriptor descriptor)
    {
        try
        {
            await StartPluginAppRuntimeAsync(descriptor);
            ClearPluginAppRuntimeFailureState(descriptor.Id);
            QueuePluginStatusUiRefresh();
        }
        catch (PluginAppRuntimeOwnershipCanceledException)
        {
            // The last entity paper disappeared, a failed WebView requested a clean replacement,
            // or PaperTodo began shutting down while startup was in flight. None is a plugin fault.
        }
        catch (Exception ex)
        {
            var failureCount =
                _pluginAppRuntimeStartFailureCounts.GetValueOrDefault(descriptor.Id) + 1;
            _pluginAppRuntimeStartFailureCounts[descriptor.Id] = failureCount;
            _pluginAppRuntimeStartFailures.Add(descriptor.Id);
            SchedulePluginAppRuntimeRetry(descriptor.Id, failureCount);
            QueuePluginStatusUiRefresh();
            Trace.TraceWarning(
                "Plugin app runtime failed to start. Provider={0}; Attempt={1}; Exception={2}",
                descriptor.Id,
                failureCount,
                ex.GetBaseException());
        }
        finally
        {
            _pluginAppRuntimeStarts.Remove(descriptor.Id);
            // 0 -> 1 can occur while an earlier async start is cancelling. Re-evaluate after the
            // in-flight marker is gone so the new entity paper cannot be stranded without runtime.
            ReconcilePluginAppRuntimes();
        }
    }

    private void SchedulePluginAppRuntimeRetry(string providerId, int failureCount)
    {
        var retryIndex = failureCount - 1;
        if (retryIndex < 0 ||
            retryIndex >= PluginAppRuntimeRetryDelays.Length ||
            _pluginAppRuntimeRetryTokens.ContainsKey(providerId) ||
            !_pluginAppRuntimeReconciliationEnabled ||
            _pluginAppRuntimeDisposing ||
            IsExiting ||
            !HasEntityPluginPaper(providerId))
        {
            return;
        }

        var token = ++_pluginAppRuntimeNextRetryToken;
        _pluginAppRuntimeRetryTokens[providerId] = token;
        _ = RetryPluginAppRuntimeAfterDelayAsync(
            providerId,
            token,
            PluginAppRuntimeRetryDelays[retryIndex]);
    }

    private async Task RetryPluginAppRuntimeAfterDelayAsync(
        string providerId,
        int token,
        TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!_pluginAppRuntimeRetryTokens.TryGetValue(providerId, out var currentToken) ||
                    currentToken != token)
                {
                    return;
                }
                _pluginAppRuntimeRetryTokens.Remove(providerId);

                if (_pluginAppRuntimeDisposing ||
                    IsExiting ||
                    !_pluginAppRuntimeReconciliationEnabled ||
                    !HasEntityPluginPaper(providerId))
                {
                    ClearPluginAppRuntimeFailureState(providerId);
                    return;
                }

                // Release the failure gate only when its backoff has elapsed. A new failure records
                // the next attempt and schedules the next bounded delay.
                _pluginAppRuntimeStartFailures.Remove(providerId);
                ReconcilePluginAppRuntimes();
            }),
            DispatcherPriority.Background);
    }

    private void ClearPluginAppRuntimeFailureState(string providerId)
    {
        _pluginAppRuntimeStartFailures.Remove(providerId);
        _pluginAppRuntimeStartFailureCounts.Remove(providerId);
        // Removing the token also invalidates a delayed retry callback that has not dispatched yet.
        _pluginAppRuntimeRetryTokens.Remove(providerId);
    }

    private async Task StartPluginAppRuntimeAsync(PaperBodyPluginDescriptor descriptor)
    {
        if (_pluginAppRuntimes.ContainsKey(descriptor.Id) ||
            !_pluginAppRuntimeReconciliationEnabled ||
            _pluginAppRuntimeDisposing ||
            IsExiting ||
            !HasEntityPluginPaper(descriptor.Id))
        {
            throw new PluginAppRuntimeOwnershipCanceledException(
                "The plugin app runtime no longer has an entity-paper owner.");
        }

        var lifetime = new PluginAppRuntimeLifetime();
        bool IsActive() =>
            lifetime.Active &&
            IsRunning &&
            _pluginAppRuntimeReconciliationEnabled &&
            !_pluginAppRuntimeDisposing &&
            HasEntityPluginPaper(descriptor.Id);
        var runtimeId = Guid.NewGuid();
        var workspace = new PaperAppRuntimeWorkspaceApi(
            this,
            descriptor.Id,
            descriptor.Permissions,
            IsActive);
        var globalTopBar = new PaperAppRuntimeGlobalTopBarApi(
            this,
            runtimeId,
            descriptor.Id,
            IsActive);
        var globalShortcuts = new PaperAppRuntimeGlobalShortcutApi(
            this,
            runtimeId,
            descriptor.Id,
            IsActive);

        IDisposable? runtime = null;
        IPaperBodyPlugin? nativeFactory = null;
        try
        {
            if (descriptor.Kind == PaperBodyPluginKind.Native)
            {
                var activation = PaperBodyPlugins.CreateNativePlugin(descriptor);
                nativeFactory = activation.Plugin;
                if (activation.Plugin is not IPaperAppRuntimeProvider provider)
                {
                    throw new InvalidOperationException(
                        $"Native plugin '{descriptor.Id}' declares appRuntime but does not implement IPaperAppRuntimeProvider.");
                }
                runtime = provider.CreateAppRuntime(new PaperAppRuntimeContext
                {
                    ProviderId = descriptor.Id,
                    ApiVersion = descriptor.ApiVersion,
                    GrantedPermissions = descriptor.Permissions,
                    Workspace = workspace,
                    GlobalTopBar = globalTopBar,
                    GlobalShortcuts = globalShortcuts
                }) ?? throw new InvalidOperationException(
                    $"Native plugin '{descriptor.Id}' returned no app runtime.");
            }
            else if (descriptor.Kind == PaperBodyPluginKind.Web)
            {
                var webRuntime = new WebPluginAppRuntime(
                    descriptor,
                    workspace,
                    globalTopBar,
                    globalShortcuts,
                    IsActive,
                    () => RequestPluginAppRuntimeRestart(runtimeId, descriptor.Id));
                runtime = webRuntime;
                await webRuntime.StartAsync();
            }
            else
            {
                throw new InvalidOperationException(
                    "Built-in body providers cannot declare plugin appRuntime.");
            }

            if (!IsActive())
            {
                throw new PluginAppRuntimeOwnershipCanceledException(
                    "The plugin app runtime lost its last entity paper while starting.");
            }

            if (_pluginAppRuntimeRestartRequests.TryGetValue(
                    descriptor.Id,
                    out var requestedRuntimeId))
            {
                _pluginAppRuntimeRestartRequests.Remove(descriptor.Id);
                if (requestedRuntimeId == runtimeId)
                {
                    throw new PluginAppRuntimeOwnershipCanceledException(
                        "The plugin app runtime requested replacement while starting.");
                }
            }

            _pluginAppRuntimes.Add(descriptor.Id, new PluginAppRuntimeLease
            {
                RuntimeId = runtimeId,
                ProviderId = descriptor.Id,
                Lifetime = lifetime,
                Workspace = workspace,
                GlobalTopBar = globalTopBar,
                GlobalShortcuts = globalShortcuts,
                Runtime = runtime,
                NativeFactory = nativeFactory
            });
        }
        catch
        {
            lifetime.Active = false;
            try { runtime?.Dispose(); } catch { }
            try { globalShortcuts.Dispose(); } catch { }
            try { globalTopBar.Dispose(); } catch { }
            try { workspace.Dispose(); } catch { }
            if (nativeFactory is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            throw;
        }
    }

    private void RequestPluginAppRuntimeRestart(Guid runtimeId, string providerId)
    {
        if (_pluginAppRuntimeDisposing || IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        // ProcessFailed is raised by WebView2. Defer disposal out of that callback so we never tear
        // down the control while WebView2 is still unwinding its own failure notification.
        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_pluginAppRuntimeDisposing || IsExiting)
                {
                    return;
                }

                if (_pluginAppRuntimes.TryGetValue(providerId, out var lease) &&
                    lease.RuntimeId == runtimeId)
                {
                    _pluginAppRuntimes.Remove(providerId);
                    ClearPluginAppRuntimeFailureState(providerId);
                    _pluginAppRuntimeRestartRequests.Remove(providerId);
                    lease.Dispose();
                    QueuePluginStatusUiRefresh();
                    ReconcilePluginAppRuntimes();
                    return;
                }

                if (_pluginAppRuntimeStarts.Contains(providerId))
                {
                    // StartAsync returns before the first document necessarily settles. Remember
                    // the exact runtime id so a stale crash callback can never cancel a newer start.
                    _pluginAppRuntimeRestartRequests[providerId] = runtimeId;
                }
            }),
            DispatcherPriority.Background);
    }

    private static bool DeclaresPluginAppRuntime(PaperBodyPluginDescriptor descriptor) =>
        descriptor.Manifest?.Capabilities?.Any(value =>
            string.Equals(
                value?.Trim(),
                PluginAppRuntimeCapability,
                StringComparison.Ordinal)) == true;

    private void DisposePluginAppRuntimes()
    {
        _pluginAppRuntimeDisposing = true;
        _pluginAppRuntimeReconciliationEnabled = false;
        foreach (var lease in _pluginAppRuntimes.Values.ToArray())
        {
            lease.Dispose();
        }
        _pluginAppRuntimes.Clear();
        _pluginAppRuntimeStartFailures.Clear();
        _pluginAppRuntimeStartFailureCounts.Clear();
        _pluginAppRuntimeRetryTokens.Clear();
        _pluginAppRuntimeRestartRequests.Clear();
    }
}
