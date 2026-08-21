using System.Diagnostics;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string PluginAppRuntimeCapability = "appRuntime";

    private sealed class PluginAppRuntimeLifetime
    {
        public bool Active { get; set; } = true;
    }

    private sealed class PluginAppRuntimeLease : IDisposable
    {
        public required string ProviderId { get; init; }
        public required PluginAppRuntimeLifetime Lifetime { get; init; }
        public required PaperAppRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperAppRuntimeGlobalTopBarApi GlobalTopBar { get; init; }
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
            _pluginAppRuntimeStartFailures.Remove(providerId);
            runtimeSetChanged = true;
        }

        foreach (var providerId in _pluginAppRuntimeStartFailures
                     .Where(providerId => !desired.ContainsKey(providerId))
                     .ToArray())
        {
            _pluginAppRuntimeStartFailures.Remove(providerId);
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
            QueuePluginStatusUiRefresh();
        }
        catch (OperationCanceledException)
        {
            // The last entity paper disappeared or PaperTodo began shutting down while startup was
            // in flight. That is a normal ownership change, not a plugin failure.
        }
        catch (Exception ex)
        {
            _pluginAppRuntimeStartFailures.Add(descriptor.Id);
            QueuePluginStatusUiRefresh();
            Trace.TraceWarning(
                "Plugin app runtime failed to start. Provider={0}; Exception={1}",
                descriptor.Id,
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

    private async Task StartPluginAppRuntimeAsync(PaperBodyPluginDescriptor descriptor)
    {
        if (_pluginAppRuntimes.ContainsKey(descriptor.Id) ||
            !_pluginAppRuntimeReconciliationEnabled ||
            _pluginAppRuntimeDisposing ||
            IsExiting ||
            !HasEntityPluginPaper(descriptor.Id))
        {
            throw new OperationCanceledException(
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
                    GlobalTopBar = globalTopBar
                }) ?? throw new InvalidOperationException(
                    $"Native plugin '{descriptor.Id}' returned no app runtime.");
            }
            else if (descriptor.Kind == PaperBodyPluginKind.Web)
            {
                var webRuntime = new WebPluginAppRuntime(
                    descriptor,
                    workspace,
                    globalTopBar,
                    IsActive);
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
                throw new OperationCanceledException(
                    "The plugin app runtime lost its last entity paper while starting.");
            }

            _pluginAppRuntimes.Add(descriptor.Id, new PluginAppRuntimeLease
            {
                ProviderId = descriptor.Id,
                Lifetime = lifetime,
                Workspace = workspace,
                GlobalTopBar = globalTopBar,
                Runtime = runtime,
                NativeFactory = nativeFactory
            });
        }
        catch
        {
            lifetime.Active = false;
            try { runtime?.Dispose(); } catch { }
            try { globalTopBar.Dispose(); } catch { }
            try { workspace.Dispose(); } catch { }
            if (nativeFactory is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            throw;
        }
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
    }
}
