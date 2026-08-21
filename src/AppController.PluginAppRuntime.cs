using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
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
    private bool _pluginAppRuntimeStartupScheduled;
    private bool _pluginAppRuntimesStarted;

    // Field initialization happens before the constructor body, but the dispatcher callback cannot
    // run until the current construction/startup stack yields. By then Current and the registry are
    // initialized. This keeps app-runtime startup independent of paper restoration without adding
    // another special case to the already-large StartAsync orchestration.
    private readonly object _pluginAppRuntimeStartupHook = QueuePluginAppRuntimeStartup();

    private static object QueuePluginAppRuntimeStartup()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)(() =>
                {
                    var controller = Current;
                    if (controller == null || !controller.IsRunning)
                    {
                        return;
                    }
                    controller.SchedulePluginAppRuntimeStartup();
                }));
        }
        return new object();
    }

    private void SchedulePluginAppRuntimeStartup()
    {
        if (_pluginAppRuntimeStartupScheduled || _pluginAppRuntimesStarted || IsExiting)
        {
            return;
        }
        _pluginAppRuntimeStartupScheduled = true;
        _ = StartPluginAppRuntimesAsync();
    }

    private async Task StartPluginAppRuntimesAsync()
    {
        if (_pluginAppRuntimesStarted || IsExiting)
        {
            return;
        }
        _pluginAppRuntimesStarted = true;
        _pluginAppRuntimeStartupScheduled = false;

        foreach (var descriptor in PaperBodyPlugins.Descriptors
                     .Where(DeclaresPluginAppRuntime)
                     .ToArray())
        {
            if (IsExiting)
            {
                return;
            }
            try
            {
                await StartPluginAppRuntimeAsync(descriptor);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "Plugin app runtime failed to start. Provider={0}; Exception={1}",
                    descriptor.Id,
                    ex.GetBaseException());
            }
        }
    }

    private async Task StartPluginAppRuntimeAsync(PaperBodyPluginDescriptor descriptor)
    {
        if (_pluginAppRuntimes.ContainsKey(descriptor.Id))
        {
            return;
        }

        var lifetime = new PluginAppRuntimeLifetime();
        bool IsActive() => lifetime.Active && IsRunning;
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

            // Shutdown can run while a WebView2 runtime is awaiting initialization. Never publish a
            // late runtime after the controller has already begun tearing plugin infrastructure down.
            if (IsExiting || !lifetime.Active)
            {
                throw new OperationCanceledException(
                    "PaperTodo is shutting down while the plugin app runtime starts.");
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
        _pluginAppRuntimesStarted = true;
        _pluginAppRuntimeStartupScheduled = false;
        foreach (var lease in _pluginAppRuntimes.Values.ToArray())
        {
            lease.Dispose();
        }
        _pluginAppRuntimes.Clear();
    }
}
