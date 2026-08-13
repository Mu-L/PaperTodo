using System.Diagnostics;
using System.Windows.Threading;
using Vortice.DirectComposition;

namespace PaperTodo;

/// <summary>
/// One active compositor session for one monitor/edge queue. The expensive DComp device, output
/// HWND and target are reused; the session owns only its immutable root, live wrappers and clocks.
/// </summary>
internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private readonly EdgeCapsuleQueueProxyPlan _plan;
    private readonly IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> _members;
    private readonly SharedRuntime _runtime;
    private readonly QueueHost _host;
    private readonly EdgeCapsuleQueueProxyWindow _window;
    private readonly IDCompositionDesktopDevice _device;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _root;
    private readonly DeviceScreenRect _outputBounds;
    private readonly List<VisualState> _visuals = new();
    private readonly HashSet<IntPtr> _cloakedRealSourceHandles = new();
    private readonly DispatcherTimer _sampleTimer;
    private readonly DispatcherTimer _completionTimer;
    private readonly Action<DeviceScreenPoint, int> _interactionRequested;
    private readonly Action _environmentChanged;
    private readonly Action<EdgeCapsuleQueueCompositionProxy, bool> _completed;
    private readonly long _sessionOrdinal;
    private long _animationStartedAtTimestamp;
    private bool _sourcesReleased;
    private bool _realEndpointMutationStarted;
    private bool _abortQueued;
    private bool _completionRetrySuccess = true;
    private int _completionRetryCount;
    private bool _finishing;
    private bool _disposed;
    private bool _starting = true;
    private bool _completionPendingDuringStart;
    private bool _pendingStartCompletionSuccess = true;
    private bool _coverLost;

    private EdgeCapsuleQueueCompositionProxy(
        long sessionOrdinal,
        EdgeCapsuleQueueProxyPlan plan,
        IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> members,
        SharedRuntime runtime,
        QueueHost host,
        IDCompositionVisual root,
        Action<DeviceScreenPoint, int> interactionRequested,
        Action environmentChanged,
        Action<EdgeCapsuleQueueCompositionProxy, bool> completed)
    {
        _plan = plan;
        _members = members;
        _runtime = runtime;
        _host = host;
        _window = host.Window;
        _device = runtime.Device;
        _target = host.Target;
        _root = root;
        _outputBounds = EdgeCapsuleQueueProxyGeometry.OutputBounds(plan.Envelope);
        _interactionRequested = interactionRequested;
        _environmentChanged = environmentChanged;
        _completed = completed;
        _sessionOrdinal = sessionOrdinal;
        var dispatcher = members[0].Window.Dispatcher;
        _sampleTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Input,
            OnSampleTimerTick,
            dispatcher);
        _completionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(plan.DurationMilliseconds + 34),
            DispatcherPriority.Render,
            OnCompletionTimerTick,
            dispatcher)
        {
            IsEnabled = false
        };
    }

    public string QueueKey => _plan.QueueKey;
    public IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> Members => _members;
    public long SessionOrdinal => _sessionOrdinal;
    public bool IsColdSession => _sessionOrdinal == 1;
    public bool CoverLost => _coverLost;
    public IntPtr OutputHandle => _disposed ? IntPtr.Zero : _window.Handle;

    internal static void Prewarm(Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownStarted)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => Prewarm(dispatcher)));
            return;
        }
        if (TryGetRuntime(dispatcher, out var runtime))
        {
            runtime.PrewarmOutputHost();
        }
    }

    internal static void PrewarmQueue(
        Dispatcher dispatcher,
        string queueKey,
        bool topmost,
        DeviceScreenRect initialBounds)
    {
        if (dispatcher.HasShutdownStarted ||
            string.IsNullOrWhiteSpace(queueKey) ||
            initialBounds.IsEmpty)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => PrewarmQueue(
                    dispatcher,
                    queueKey,
                    topmost,
                    initialBounds)));
            return;
        }
        if (TryGetRuntime(dispatcher, out var runtime))
        {
            runtime.PrewarmQueue(queueKey, topmost, initialBounds);
        }
    }

    private static bool TryGetRuntime(
        Dispatcher dispatcher,
        out SharedRuntime runtime)
    {
        try
        {
            runtime = SharedRuntimes.GetValue(
                dispatcher,
                static key => new SharedRuntime(key));
            return runtime.IsUsable;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule shared DirectComposition runtime creation failed. Exception={0}",
                ex);
            runtime = null!;
            return false;
        }
    }

    public static long ReserveSessionOrdinal() =>
        Interlocked.Increment(ref _nextSessionOrdinal);

    public static EdgeCapsuleQueueCompositionProxy? TryCreate(
        long sessionOrdinal,
        EdgeCapsuleQueueProxyPlan plan,
        IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> members,
        Action<DeviceScreenPoint, int> interactionRequested,
        Action environmentChanged,
        Action<EdgeCapsuleQueueCompositionProxy, bool> completed)
    {
        if (members.Count == 0 ||
            members.Count != plan.Members.Count ||
            members.Any(member => member.SourceHandle == IntPtr.Zero) ||
            !TryGetRuntime(members[0].Window.Dispatcher, out var runtime))
        {
            return null;
        }

        var host = runtime.TryAcquire(
            plan.QueueKey,
            plan.Topmost,
            EdgeCapsuleQueueProxyGeometry.OutputBounds(plan.Envelope));
        if (host == null)
        {
            return null;
        }

        IDCompositionVisual? root = null;
        EdgeCapsuleQueueCompositionProxy? proxy = null;
        try
        {
            runtime.Device.CreateVisual(out IDCompositionVisual2 rootVisual).CheckError();
            root = rootVisual;
            host.Target.SetRoot(root).CheckError();
            proxy = new EdgeCapsuleQueueCompositionProxy(
                sessionOrdinal,
                plan,
                members,
                runtime,
                host,
                root,
                interactionRequested,
                environmentChanged,
                completed);
            if (!host.Attach(proxy))
            {
                throw new InvalidOperationException(
                    "The queue compositor host is already owned by another session.");
            }
            root = null;
            return proxy;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule queue DirectComposition proxy creation failed. Queue={0}; Exception={1}",
                plan.QueueKey,
                ex);
            try { host.Target.SetRoot(null!).CheckError(); } catch { }
            try { runtime.Device.Commit().CheckError(); } catch { }
            root?.Dispose();
            if (proxy != null)
            {
                host.Detach(proxy);
            }
            return null;
        }
    }
}
