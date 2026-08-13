using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed record EdgeCapsuleQueueCompositionProxyMember(
    PaperWindow Window,
    EdgeCapsuleQueueProxyMemberPlan Plan,
    IntPtr SourceHandle,
    EdgeCapsuleProxySnapshotHost? SnapshotHost);

internal enum EdgeCapsuleQueueProxyVisualLayer
{
    MovingSource = 0,
    RevealTarget = 1,
    ConcealSource = 2,
    StartSnapshot = 3
}

/// <summary>
/// Dispatcher-shared DirectComposition device, queue-scoped output hosts and generation-owned
/// visual resources. A queue host may have one current generation and one staged successor.
/// </summary>
internal sealed partial class EdgeCapsuleQueueCompositionProxy : IDisposable
{
    private sealed class VisualState : IDisposable
    {
        public required EdgeCapsuleQueueCompositionProxyMember Member { get; init; }
        public required EdgeCapsuleQueueProxyVisualLayer Layer { get; init; }
        public required IntPtr PresentedSourceHandle { get; init; }
        public required DeviceScreenRect SourceBounds { get; init; }
        public required IUnknown Surface { get; init; }
        public required IDCompositionVisual Visual { get; init; }
        public required IDCompositionEffectGroup Effect { get; init; }
        public required IDCompositionRectangleClip Clip { get; init; }
        public required float StartOffsetX { get; init; }
        public required float StartOffsetY { get; init; }
        public required float TargetOffsetX { get; init; }
        public required float TargetOffsetY { get; init; }
        public required EdgeCapsuleProxyClipRect StartClip { get; init; }
        public required EdgeCapsuleProxyClipRect TargetClip { get; init; }
        public required float StartOpacity { get; init; }
        public required float TargetOpacity { get; init; }
        public required int OpacityDurationMilliseconds { get; init; }

        public IDCompositionAnimation? OffsetXAnimation { get; set; }
        public IDCompositionAnimation? OffsetYAnimation { get; set; }
        public IDCompositionAnimation? ClipLeftAnimation { get; set; }
        public IDCompositionAnimation? ClipTopAnimation { get; set; }
        public IDCompositionAnimation? ClipRightAnimation { get; set; }
        public IDCompositionAnimation? ClipBottomAnimation { get; set; }
        public IDCompositionAnimation? OpacityAnimation { get; set; }

        public void Dispose()
        {
            OpacityAnimation?.Dispose();
            ClipBottomAnimation?.Dispose();
            ClipRightAnimation?.Dispose();
            ClipTopAnimation?.Dispose();
            ClipLeftAnimation?.Dispose();
            OffsetYAnimation?.Dispose();
            OffsetXAnimation?.Dispose();
            Clip.Dispose();
            Effect.Dispose();
            Visual.Dispose();
            Surface.Dispose();
        }
    }

    private sealed class QueueHost : IDisposable
    {
        private readonly SharedRuntime _runtime;
        private bool _disposed;

        private QueueHost(
            SharedRuntime runtime,
            string queueKey,
            EdgeCapsuleQueueProxyWindow window,
            IDCompositionTarget target)
        {
            _runtime = runtime;
            QueueKey = queueKey;
            Window = window;
            Target = target;
        }

        public string QueueKey { get; private set; }
        public EdgeCapsuleQueueProxyWindow Window { get; }
        public IDCompositionTarget Target { get; }
        public EdgeCapsuleQueueCompositionProxy? Current { get; private set; }
        public EdgeCapsuleQueueCompositionProxy? Staged { get; private set; }
        public bool IsAvailable =>
            !_disposed &&
            Current == null &&
            Staged == null &&
            Window.Handle != IntPtr.Zero;

        public static QueueHost? TryCreate(
            SharedRuntime runtime,
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds)
        {
            if (initialBounds.IsEmpty)
            {
                return null;
            }

            QueueHost? host = null;
            EdgeCapsuleQueueProxyWindow? window = null;
            IDCompositionTarget? target = null;
            try
            {
                window = EdgeCapsuleQueueProxyWindow.TryCreate(
                    initialBounds,
                    topmost,
                    point => host?.Current?.ContainsVisual(point) == true,
                    (point, message) =>
                        host?.Current?.HandleInteractionRequested(point, message),
                    () => host?.Current?.HandleEnvironmentChanged(),
                    () => host?.Current?.HandleCompositionPaint(),
                    () => host?.Current?.HandleOutputLost());
                if (window == null)
                {
                    return null;
                }

                runtime.Device.CreateTargetForHwnd(
                    window.Handle,
                    topmost: true,
                    out target).CheckError();
                host = new QueueHost(runtime, queueKey, window, target);
                window = null;
                target = null;
                return host;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "Edge capsule queue compositor host creation failed. Queue={0}; Exception={1}",
                    queueKey,
                    ex);
                target?.Dispose();
                window?.Dispose();
                return null;
            }
        }

        public bool AssignQueue(string queueKey)
        {
            if (_disposed ||
                Current != null ||
                Staged != null ||
                string.IsNullOrWhiteSpace(queueKey))
            {
                return false;
            }
            QueueKey = queueKey;
            return true;
        }

        public bool ReleaseQueue()
        {
            if (!IsAvailable)
            {
                return false;
            }
            QueueKey = string.Empty;
            return true;
        }

        public bool CanStage(
            EdgeCapsuleQueueCompositionProxy? predecessor) =>
            !_disposed &&
            Window.Handle != IntPtr.Zero &&
            Staged == null &&
            (predecessor == null
                ? Current == null
                : ReferenceEquals(Current, predecessor));

        public bool TryStage(
            EdgeCapsuleQueueCompositionProxy proxy,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            if (!CanStage(predecessor))
            {
                return false;
            }
            Staged = proxy;
            return true;
        }

        public bool Promote(
            EdgeCapsuleQueueCompositionProxy proxy,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            if (_disposed ||
                !ReferenceEquals(Staged, proxy) ||
                (predecessor == null
                    ? Current != null
                    : !ReferenceEquals(Current, predecessor)))
            {
                return false;
            }

            Current = proxy;
            Staged = null;
            return true;
        }

        public bool RollbackPromotion(
            EdgeCapsuleQueueCompositionProxy proxy,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            if (_disposed)
            {
                return false;
            }

            if (ReferenceEquals(Staged, proxy))
            {
                Staged = null;
            }

            if (ReferenceEquals(Current, proxy))
            {
                Current = predecessor;
            }
            else if (predecessor != null &&
                     !ReferenceEquals(Current, predecessor))
            {
                return false;
            }

            if (Current == null && Staged == null)
            {
                Window.Hide();
            }
            return true;
        }

        public void Detach(EdgeCapsuleQueueCompositionProxy proxy)
        {
            if (ReferenceEquals(Staged, proxy))
            {
                Staged = null;
            }
            if (ReferenceEquals(Current, proxy))
            {
                Current = null;
            }
            if (Current == null && Staged == null)
            {
                Window.Hide();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Current = null;
            Staged = null;
            try { Target.SetRoot(null!).CheckError(); } catch { }
            try { _runtime.Device.Commit().CheckError(); } catch { }
            try { Target.Dispose(); } catch { }
            try { Window.Dispose(); } catch { }
        }
    }

    private sealed class SharedRuntime : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<string, QueueHost> _hosts =
            new(StringComparer.Ordinal);
        private readonly Stack<QueueHost> _spareHosts = new();
        private bool _disposed;
        private bool _invalid;

        internal SharedRuntime(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            var desktopDeviceId = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(
                IntPtr.Zero,
                ref desktopDeviceId,
                out var devicePointer));
            Device = new IDCompositionDesktopDevice(devicePointer);
            _dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        }

        internal IDCompositionDesktopDevice Device { get; }
        internal bool IsUsable => !_disposed && !_invalid;

        internal void PrewarmOutputHost()
        {
            _dispatcher.VerifyAccess();
            if (!IsUsable || _spareHosts.Count > 0)
            {
                return;
            }

            var offscreen =
                new DeviceScreenRect(-32000, -32000, -31996, -31996);
            var host = QueueHost.TryCreate(
                this,
                string.Empty,
                topmost: true,
                offscreen);
            if (host != null)
            {
                _spareHosts.Push(host);
            }
        }

        private QueueHost? TakeOrCreateHost(
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds)
        {
            QueueHost? host = null;
            while (_spareHosts.Count > 0 && host == null)
            {
                var candidate = _spareHosts.Pop();
                if (candidate.IsAvailable &&
                    candidate.AssignQueue(queueKey))
                {
                    host = candidate;
                }
                else
                {
                    try { candidate.Dispose(); } catch { }
                }
            }
            return host ?? QueueHost.TryCreate(
                this,
                queueKey,
                topmost,
                initialBounds);
        }

        internal void PrewarmQueue(
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds)
        {
            _dispatcher.VerifyAccess();
            if (!IsUsable || _hosts.ContainsKey(queueKey))
            {
                return;
            }
            var host = TakeOrCreateHost(
                queueKey,
                topmost,
                initialBounds);
            if (host != null)
            {
                _hosts[queueKey] = host;
            }
        }

        internal QueueHost? TryAcquire(
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            _dispatcher.VerifyAccess();
            if (!IsUsable)
            {
                return null;
            }
            if (!_hosts.TryGetValue(queueKey, out var host))
            {
                if (predecessor != null)
                {
                    return null;
                }
                host = TakeOrCreateHost(
                    queueKey,
                    topmost,
                    initialBounds);
                if (host == null)
                {
                    return null;
                }
                _hosts[queueKey] = host;
            }
            return host.CanStage(predecessor) ? host : null;
        }

        internal void Release(
            QueueHost host,
            EdgeCapsuleQueueCompositionProxy proxy,
            bool broken)
        {
            _dispatcher.VerifyAccess();
            host.Detach(proxy);
            if (_disposed)
            {
                return;
            }

            if (!broken)
            {
                ReturnIdleHost(host);
                return;
            }

            _invalid = true;
            foreach (var cached in _hosts.Values.ToArray())
            {
                try { cached.Dispose(); } catch { }
            }
            _hosts.Clear();
            while (_spareHosts.Count > 0)
            {
                try { _spareHosts.Pop().Dispose(); } catch { }
            }
            SharedRuntimes.Remove(_dispatcher);
            try { Device.Dispose(); } catch { }
            _disposed = true;
        }

        internal void ReturnIdleHost(QueueHost host)
        {
            _dispatcher.VerifyAccess();
            if (_disposed || !host.IsAvailable)
            {
                return;
            }

            // Queue identity is an active-session concern, not a permanent cache key. Retain at
            // most one dispatcher-wide spare; this preserves a warm DComp target without growing
            // one transparent output HWND per monitor/edge queue.
            if (_hosts.TryGetValue(host.QueueKey, out var cached) &&
                ReferenceEquals(cached, host))
            {
                _hosts.Remove(host.QueueKey);
            }
            if (_spareHosts.Count == 0 && host.ReleaseQueue())
            {
                _spareHosts.Push(host);
            }
            else
            {
                try { host.Dispose(); } catch { }
            }
        }

        private void OnDispatcherShutdownStarted(
            object? sender,
            EventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
            foreach (var host in _hosts.Values.ToArray())
            {
                try { host.Dispose(); } catch { }
            }
            _hosts.Clear();
            while (_spareHosts.Count > 0)
            {
                try { _spareHosts.Pop().Dispose(); } catch { }
            }
            try { Device.Dispose(); } catch { }
        }
    }

    private static readonly ConditionalWeakTable<Dispatcher, SharedRuntime>
        SharedRuntimes = new();
    private static long _nextSessionOrdinal;
}
