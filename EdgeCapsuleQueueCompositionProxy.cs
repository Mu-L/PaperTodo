using System.Diagnostics;
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

/// <summary>
/// One DirectComposition target for one active monitor/edge queue. The target and every source
/// wrapper are all-or-nothing: any failure uncloaks the real HWNDs and returns control to WPF.
/// </summary>
internal sealed class EdgeCapsuleQueueCompositionProxy : IDisposable
{
    private sealed class VisualState : IDisposable
    {
        public required EdgeCapsuleQueueCompositionProxyMember Member { get; set; }
        public required IntPtr PresentedSourceHandle { get; set; }
        public required IUnknown Surface { get; set; }
        public required IDCompositionVisual Visual { get; set; }
        public IDCompositionEffectGroup? Effect { get; set; }
        public IDCompositionScaleTransform? Scale { get; set; }
        public IDCompositionAnimation? OffsetXAnimation { get; set; }
        public IDCompositionAnimation? OffsetYAnimation { get; set; }
        public IDCompositionAnimation? ScaleXAnimation { get; set; }
        public IDCompositionAnimation? ScaleYAnimation { get; set; }
        public IDCompositionAnimation? OpacityAnimation { get; set; }
        public bool IsEndpointLayer { get; set; }
        public bool OwnsCoreResources { get; set; } = true;

        public void Dispose()
        {
            OpacityAnimation?.Dispose();
            ScaleYAnimation?.Dispose();
            ScaleXAnimation?.Dispose();
            OffsetYAnimation?.Dispose();
            OffsetXAnimation?.Dispose();
            Scale?.Dispose();
            Effect?.Dispose();
            if (OwnsCoreResources)
            {
                Visual.Dispose();
                Surface.Dispose();
            }
        }
    }

    private readonly EdgeCapsuleQueueProxyPlan _plan;
    private readonly IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> _members;
    private readonly EdgeCapsuleQueueProxyWindow _window;
    private readonly IDCompositionDesktopDevice _device;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _root;
    private readonly List<VisualState> _visuals = new();
    private readonly HashSet<IntPtr> _cloakedRealSourceHandles = new();
    private readonly DispatcherTimer _sampleTimer;
    private readonly DispatcherTimer _completionTimer;
    private readonly Action<EdgeCapsuleQueueCompositionProxy, bool> _completed;
    private static long _nextSessionOrdinal;
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
        EdgeCapsuleQueueProxyWindow window,
        IDCompositionDesktopDevice device,
        IDCompositionTarget target,
        IDCompositionVisual root,
        Action<EdgeCapsuleQueueCompositionProxy, bool> completed)
    {
        _plan = plan;
        _members = members;
        _window = window;
        _device = device;
        _target = target;
        _root = root;
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
            members.Any(member => member.SourceHandle == IntPtr.Zero))
        {
            return null;
        }

        EdgeCapsuleQueueProxyWindow? proxyWindow = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual? root = null;
        EdgeCapsuleQueueCompositionProxy? proxy = null;
        try
        {
            proxyWindow = EdgeCapsuleQueueProxyWindow.TryCreate(
                plan.Envelope,
                plan.Topmost,
                point => proxy?.ContainsVisual(point) == true,
                interactionRequested,
                environmentChanged,
                () => proxy?.HandleCompositionPaint(),
                () => proxy?.HandleOutputLost());
            if (proxyWindow == null)
            {
                return null;
            }

            // CreateSurfaceFromHwnd does not need a caller-provided D3D device. Supplying null
            // avoids a second graphics-device lifetime and keeps the proxy deployable as a normal
            // single-file PaperTodo build.
            var desktopDeviceId = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(
                IntPtr.Zero,
                ref desktopDeviceId,
                out var devicePointer));
            device = new IDCompositionDesktopDevice(devicePointer);
            device.CreateTargetForHwnd(
                proxyWindow.Handle,
                topmost: true,
                out target).CheckError();
            device.CreateVisual(out IDCompositionVisual2 rootVisual).CheckError();
            root = rootVisual;
            target.SetRoot(root).CheckError();
            proxy = new EdgeCapsuleQueueCompositionProxy(
                sessionOrdinal,
                plan,
                members,
                proxyWindow,
                device,
                target,
                root,
                completed);
            proxyWindow = null;
            device = null;
            target = null;
            root = null;
            return proxy;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule queue DirectComposition proxy failed. Queue={0}; Exception={1}",
                plan.QueueKey,
                ex);
            if (proxy != null)
            {
                proxy.ForceDisposeForShutdown();
                root = null;
                target = null;
                device = null;
                proxyWindow = null;
            }
            root?.Dispose();
            target?.Dispose();
            device?.Dispose();
            proxyWindow?.Dispose();
            return null;
        }
    }

    public bool TryStart(out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        var started = false;
        try
        {
            started = PrepareAndStart();
            realHostMayHaveChanged = _realEndpointMutationStarted;
        }
        catch (Exception ex)
        {
            realHostMayHaveChanged = _realEndpointMutationStarted;
            Trace.TraceWarning(
                "Edge capsule queue DirectComposition proxy start failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        finally
        {
            _starting = false;
        }
        if (started && _completionPendingDuringStart)
        {
            var pendingSuccess = _pendingStartCompletionSuccess;
            _ = _members[0].Window.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                (Action)(() => CompleteNow(pendingSuccess)));
        }
        return started;
    }

    private void HandleCompositionPaint()
    {
        if (_disposed || _sourcesReleased)
        {
            return;
        }
        try
        {
            using var baseDevice = _device.QueryInterface<IDCompositionDevice>();
            baseDevice.CheckDeviceState(out var valid).CheckError();
            if (valid)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue composition device check failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        HandleOutputLost();
    }

    private void HandleOutputLost()
    {
        _coverLost = true;
        CompleteNow(success: false);
    }

    public bool TryGetPresentation(
        PaperWindow window,
        out EdgeCapsulePresentationFrame frame)
    {
        if (_coverLost)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }
        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (member == null)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }
        frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
            member.Plan,
            AnimationStartedAtTimestamp,
            _plan.DurationMilliseconds,
            Stopwatch.GetTimestamp());
        return true;
    }

    public bool RetainsSource(PaperWindow window) =>
        !_disposed &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window) &&
            // Every member is a live CreateSurfaceFromHwnd source during a retained/retrying
            // session. Clearing opening content is just as destructive as clearing closing content.
            member.SourceHandle != IntPtr.Zero);

    public bool Routes(PaperWindow window) =>
        !_disposed && _members.Any(member => ReferenceEquals(member.Window, window));

    public IntPtr SourceHandleFor(PaperWindow window) =>
        _members.FirstOrDefault(member => ReferenceEquals(member.Window, window))
            ?.SourceHandle ?? IntPtr.Zero;

    public bool TryRouteApply(
        PaperWindow window,
        EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || _coverLost)
        {
            return false;
        }
        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (member == null)
        {
            return false;
        }
        if (member.Plan.Target == frame)
        {
            return true;
        }

        // A new reducer/layout generation superseded this immutable animation plan. Let the new
        // frame reach the cloaked real host, then abort the old proxy after the current reconcile
        // unwinds; claiming it was presented would create a second applied-frame truth.
        QueueAbortAfterCurrentApply();
        return false;
    }

    public bool TryResolveInputTarget(
        DeviceScreenPoint point,
        out IntPtr targetHandle,
        out DeviceScreenPoint endpointPoint)
    {
        if (_coverLost)
        {
            targetHandle = IntPtr.Zero;
            endpointPoint = point;
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        foreach (var member in _members)
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                continue;
            }
            var current = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                member.Plan,
                AnimationStartedAtTimestamp,
                _plan.DurationMilliseconds,
                now);
            if (current.IsHitTestVisible &&
                !current.InteractiveBounds.IsEmpty &&
                EdgeCapsuleGeometry.Contains(current.InteractiveBounds, point))
            {
                targetHandle = member.SourceHandle;
                endpointPoint = MapPoint(
                    point,
                    current.InteractiveBounds,
                    member.Plan.Target.InteractiveBounds.IsEmpty
                        ? member.Plan.Target.Bounds
                        : member.Plan.Target.InteractiveBounds);
                return targetHandle != IntPtr.Zero;
            }
        }
        targetHandle = IntPtr.Zero;
        endpointPoint = point;
        return false;
    }

    private static DeviceScreenPoint MapPoint(
        DeviceScreenPoint point,
        DeviceScreenRect source,
        DeviceScreenRect target)
    {
        if (source.IsEmpty || target.IsEmpty)
        {
            return point;
        }
        var relativeX = Math.Clamp(
            (point.X - source.Left) / Math.Max(1.0, source.Width),
            0,
            1);
        var relativeY = Math.Clamp(
            (point.Y - source.Top) / Math.Max(1.0, source.Height),
            0,
            1);
        return new DeviceScreenPoint(
            target.Left + relativeX * target.Width,
            target.Top + relativeY * target.Height);
    }

    private void QueueAbortAfterCurrentApply()
    {
        if (_abortQueued || _disposed || _finishing)
        {
            return;
        }
        _abortQueued = true;
        _ = _members[0].Window.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            (Action)(() =>
            {
                _abortQueued = false;
                CompleteNow(success: false);
            }));
    }

    public void CompleteNow(bool success)
    {
        if (_starting)
        {
            _completionPendingDuringStart = true;
            _pendingStartCompletionSuccess &= success;
            return;
        }
        if (_disposed || _finishing)
        {
            return;
        }
        _finishing = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
        try
        {
            _completed(this, success);
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue proxy completion failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            ScheduleCompletionRetry(success: false);
        }
    }

    public void ScheduleCompletionRetry(bool success)
    {
        if (_disposed)
        {
            return;
        }
        if (_sourcesReleased)
        {
            DisposeCore();
            return;
        }
        _finishing = false;
        _completionRetrySuccess = success;
        _completionRetryCount++;
        _completionTimer.Stop();
        _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _completionTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retry session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"attempt={_completionRetryCount} successTarget={success}");
#endif
    }

    private bool PrepareAndStart()
    {
#if DEBUG
        var started = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        void AddVisual(
            EdgeCapsuleQueueCompositionProxyMember member,
            IntPtr sourceHandle,
            DeviceScreenRect initialBounds,
            float initialOpacity,
            bool endpointLayer)
        {
            IUnknown? surface = null;
            IDCompositionVisual? visual = null;
            IDCompositionScaleTransform? scale = null;
            IDCompositionEffectGroup? effect = null;
            try
            {
                _device.CreateSurfaceFromHwnd(sourceHandle, out var createdSurface)
                    .CheckError();
                surface = createdSurface;
                _device.CreateVisual(out IDCompositionVisual2 createdVisual)
                    .CheckError();
                visual = createdVisual;
                visual.SetContent(surface).CheckError();

                var startX = initialBounds.Left - _plan.Envelope.Left;
                var startY = initialBounds.Top - _plan.Envelope.Top;
                visual.SetOffsetX(startX).CheckError();
                visual.SetOffsetY(startY).CheckError();
                scale = _device.CreateScaleTransform();
                scale.SetScaleX(1).CheckError();
                scale.SetScaleY(1).CheckError();
                scale.SetCenterX(0).CheckError();
                scale.SetCenterY(0).CheckError();
                visual.SetTransform(scale).CheckError();
                effect = _device.CreateEffectGroup();
                effect.SetOpacity(initialOpacity).CheckError();
                visual.SetEffect(effect).CheckError();
                var referenceVisual = _visuals.Count == 0
                    ? null
                    : _visuals[^1].Visual;
                _root.AddVisual(
                    visual,
                    insertAbove: true,
                    referenceVisual: referenceVisual!)
                    .CheckError();
                _visuals.Add(new VisualState
                {
                    Member = member,
                    PresentedSourceHandle = sourceHandle,
                    Surface = surface,
                    Visual = visual,
                    Effect = effect,
                    Scale = scale,
                    IsEndpointLayer = endpointLayer
                });
                surface = null;
                visual = null;
                scale = null;
                effect = null;
            }
            finally
            {
                effect?.Dispose();
                scale?.Dispose();
                visual?.Dispose();
                surface?.Dispose();
            }
        }

        foreach (var member in _members)
        {
            AddVisual(
                member,
                member.SnapshotHost?.Handle ?? member.SourceHandle,
                member.Plan.Start.Bounds,
                initialOpacity: 1,
                endpointLayer: false);
            if (member.Plan.Role == EdgeCapsuleQueueProxyMemberRole.OpeningPreview)
            {
                AddVisual(
                    member,
                    member.SourceHandle,
                    member.Plan.Target.Bounds,
                    initialOpacity: 0,
                    endpointLayer: true);
            }
        }

        _device.Commit().CheckError();
        _device.WaitForCommitCompletion().CheckError();
        if (!_window.Show(_plan.Envelope, _plan.Topmost))
        {
            return false;
        }
        WindowNative.FlushDesktopComposition();
        if (_coverLost)
        {
            return false;
        }

        foreach (var member in _members)
        {
            if (_coverLost)
            {
                return false;
            }
            // Track the exact real HWND before attempting the cloak. DwmSetWindowAttribute can
            // succeed while the verification read fails; that ambiguous result still requires an
            // unconditional compensating uncloak before the proxy may be removed.
            _cloakedRealSourceHandles.Add(member.SourceHandle);
            if (!WindowNative.TrySetWindowCloaked(
                    member.SourceHandle,
                    cloaked: true))
            {
                return false;
            }
            if (member.SnapshotHost != null &&
                !member.SnapshotHost.TrySetCloaked(cloaked: true))
            {
                return false;
            }
        }
        WindowNative.FlushDesktopComposition();
        if (_coverLost)
        {
            return false;
        }

        // Opening/moving endpoints are prepared underneath the proxy. Closing keeps the complete
        // live preview source unchanged until the compositor finishes, then snaps compact while
        // still cloaked; this preserves WebView2 without screenshots or reparenting.
        _realEndpointMutationStarted = true;
#if DEBUG
        var endpointStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var endpointMembers = _members
            .Where(member => !member.Plan.DefersRealEndpoint)
            .ToArray();
        var endpointReady = true;
        foreach (var member in endpointMembers)
        {
            endpointReady &= member.Window.ApplyEdgeCapsuleQueueProxyEndpoint(
                member.Plan.Target);
        }
        if (endpointReady)
        {
            foreach (var member in endpointMembers)
            {
                endpointReady &= member.Window
                    .PrepareEdgeCapsuleQueueProxyEndpointForHandoff();
            }
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.endpoint phase=prepare session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"members={endpointMembers.Length} ready={endpointReady} " +
            $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointStartedAt):F3}");
#endif
        if (!endpointReady)
        {
            return false;
        }

        for (var index = 0; index < _visuals.Count; index++)
        {
            var state = _visuals[index];
            var member = state.Member;
            if (state.IsEndpointLayer)
            {
                IDCompositionAnimation? endpointOpacity = null;
                IDCompositionAnimation? endpointOffsetX = null;
                IDCompositionAnimation? endpointOffsetY = null;
                IDCompositionAnimation? endpointScaleX = null;
                IDCompositionAnimation? endpointScaleY = null;
                try
                {
                    var startX = member.Plan.Start.Bounds.Left - _plan.Envelope.Left;
                    var startY = member.Plan.Start.Bounds.Top - _plan.Envelope.Top;
                    var targetX = member.Plan.Target.Bounds.Left - _plan.Envelope.Left;
                    var targetY = member.Plan.Target.Bounds.Top - _plan.Envelope.Top;
                    // This visual wraps the full endpoint HWND. Start it at the source bounds and
                    // grow it through exactly the same sampled geometry as the compact snapshot;
                    // opacity alone would expose the full preview outside logical hit/corridor bounds.
                    state.Visual.SetOffsetX(startX).CheckError();
                    state.Visual.SetOffsetY(startY).CheckError();
                    state.Scale!.SetScaleX(
                        member.Plan.Start.Bounds.Width /
                        (float)Math.Max(1, member.Plan.Target.Bounds.Width)).CheckError();
                    state.Scale.SetScaleY(
                        member.Plan.Start.Bounds.Height /
                        (float)Math.Max(1, member.Plan.Target.Bounds.Height)).CheckError();
                    endpointOffsetX = CreateEaseOutCubicAnimation(
                        startX,
                        targetX,
                        _plan.DurationMilliseconds);
                    endpointOffsetY = CreateEaseOutCubicAnimation(
                        startY,
                        targetY,
                        _plan.DurationMilliseconds);
                    endpointScaleX = CreateEaseOutCubicAnimation(
                        member.Plan.Start.Bounds.Width /
                            (float)Math.Max(1, member.Plan.Target.Bounds.Width),
                        1,
                        _plan.DurationMilliseconds);
                    endpointScaleY = CreateEaseOutCubicAnimation(
                        member.Plan.Start.Bounds.Height /
                            (float)Math.Max(1, member.Plan.Target.Bounds.Height),
                        1,
                        _plan.DurationMilliseconds);
                    state.Visual.SetOffsetX(endpointOffsetX).CheckError();
                    state.Visual.SetOffsetY(endpointOffsetY).CheckError();
                    state.Scale.SetScaleX(endpointScaleX).CheckError();
                    state.Scale.SetScaleY(endpointScaleY).CheckError();
                    endpointOpacity = CreateEaseOutCubicAnimation(
                        0,
                        1,
                        _plan.DurationMilliseconds);
                    state.Effect!.SetOpacity(endpointOpacity).CheckError();
                    state.OffsetXAnimation = endpointOffsetX;
                    state.OffsetYAnimation = endpointOffsetY;
                    state.ScaleXAnimation = endpointScaleX;
                    state.ScaleYAnimation = endpointScaleY;
                    state.OpacityAnimation = endpointOpacity;
                    endpointOffsetX = null;
                    endpointOffsetY = null;
                    endpointScaleX = null;
                    endpointScaleY = null;
                    endpointOpacity = null;
                }
                finally
                {
                    endpointOpacity?.Dispose();
                    endpointScaleY?.Dispose();
                    endpointScaleX?.Dispose();
                    endpointOffsetY?.Dispose();
                    endpointOffsetX?.Dispose();
                }
                continue;
            }
            var endX = member.Plan.Target.Bounds.Left - _plan.Envelope.Left;
            var endY = member.Plan.Target.Bounds.Top - _plan.Envelope.Top;
            IDCompositionAnimation? offsetX = null;
            IDCompositionAnimation? offsetY = null;
            IDCompositionAnimation? scaleX = null;
            IDCompositionAnimation? scaleY = null;
            IDCompositionAnimation? opacity = null;
            try
            {
                offsetX = CreateEaseOutCubicAnimation(
                    member.Plan.Start.Bounds.Left - _plan.Envelope.Left,
                    endX,
                    _plan.DurationMilliseconds);
                offsetY = CreateEaseOutCubicAnimation(
                    member.Plan.Start.Bounds.Top - _plan.Envelope.Top,
                    endY,
                    _plan.DurationMilliseconds);
                state.Visual.SetOffsetX(offsetX).CheckError();
                state.Visual.SetOffsetY(offsetY).CheckError();
                scaleX = CreateEaseOutCubicAnimation(
                    1,
                    member.Plan.Target.Bounds.Width /
                        (float)Math.Max(1, member.Plan.Start.Bounds.Width),
                    _plan.DurationMilliseconds);
                scaleY = CreateEaseOutCubicAnimation(
                    1,
                    member.Plan.Target.Bounds.Height /
                        (float)Math.Max(1, member.Plan.Start.Bounds.Height),
                    _plan.DurationMilliseconds);
                state.Scale!.SetScaleX(scaleX).CheckError();
                state.Scale.SetScaleY(scaleY).CheckError();

                if (member.Plan.Role == EdgeCapsuleQueueProxyMemberRole.OpeningPreview)
                {
                    opacity = CreateEaseOutCubicAnimation(
                        1,
                        0,
                        _plan.DurationMilliseconds);
                    state.Effect!.SetOpacity(opacity).CheckError();
                }
                _visuals[index] = new VisualState
                {
                    Member = state.Member,
                    PresentedSourceHandle = state.PresentedSourceHandle,
                    Surface = state.Surface,
                    Visual = state.Visual,
                    Effect = state.Effect,
                    Scale = state.Scale,
                    OffsetXAnimation = offsetX,
                    OffsetYAnimation = offsetY,
                    ScaleXAnimation = scaleX,
                    ScaleYAnimation = scaleY,
                    OpacityAnimation = opacity,
                    IsEndpointLayer = false,
                    OwnsCoreResources = true
                };
                offsetX = null;
                offsetY = null;
                scaleX = null;
                scaleY = null;
                opacity = null;
                state.Effect = null;
                state.Scale = null;
                state.OwnsCoreResources = false;
                state.Dispose();
            }
            finally
            {
                opacity?.Dispose();
                scaleY?.Dispose();
                scaleX?.Dispose();
                offsetY?.Dispose();
                offsetX?.Dispose();
            }
        }

        _device.Commit().CheckError();
        // Default DComp animations begin on the first compositor frame containing this commit.
        // Waiting before starting the logical timer is deliberately conservative: it can retain
        // the cover a few milliseconds longer, but can never tear it down before the visual ends.
        _device.WaitForCommitCompletion().CheckError();
        _animationStartedAtTimestamp = Stopwatch.GetTimestamp();
        _sampleTimer.Start();
        _completionTimer.Start();
#if DEBUG
        var envelopePixels = (long)_plan.Envelope.Width * _plan.Envelope.Height;
        var wrappedPixels = _members.Sum(member =>
            (long)member.Plan.Start.Bounds.Width * member.Plan.Start.Bounds.Height +
            (member.Plan.Role == EdgeCapsuleQueueProxyMemberRole.OpeningPreview
                ? (long)member.Plan.Target.Bounds.Width * member.Plan.Target.Bounds.Height
                : 0));
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.session phase=start session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"members={_members.Count} durationMs={_plan.DurationMilliseconds} " +
            $"envelope={_plan.Envelope.Left},{_plan.Envelope.Top}," +
            $"{_plan.Envelope.Width}x{_plan.Envelope.Height} " +
            $"prepareMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(started):F3}");
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"resource.proxy session={_sessionOrdinal} cold={IsColdSession} " +
            $"queue={_plan.QueueKey} scope=geometry-estimate excludesDwmGpu=true " +
            $"envelopePixels={envelopePixels} wrappedPixels={wrappedPixels} " +
            $"wrappedRgbaEstimateMiB={wrappedPixels * 4 / (1024.0 * 1024.0):F3} " +
            $"snapshotHosts={_members.Count(member => member.SnapshotHost != null)}");
#endif
        return true;
    }

    private IDCompositionAnimation CreateEaseOutCubicAnimation(
        float from,
        float to,
        int durationMilliseconds)
    {
        var durationSeconds = Math.Max(0.001, durationMilliseconds / 1000.0);
        var delta = to - from;
        var animation = _device.CreateAnimation();
        try
        {
            // f(t)=from+delta*(1-(1-t/d)^3) expanded into DComp's cubic coefficients.
            animation.AddCubic(
                0,
                from,
                (float)(3 * delta / durationSeconds),
                (float)(-3 * delta / (durationSeconds * durationSeconds)),
                (float)(delta / (durationSeconds * durationSeconds * durationSeconds)))
                .CheckError();
            animation.End(durationSeconds, to).CheckError();
            return animation;
        }
        catch
        {
            animation.Dispose();
            throw;
        }
    }

    private bool ContainsVisual(DeviceScreenPoint point)
    {
        if (_coverLost)
        {
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        return _members.Any(member =>
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                return false;
            }
            var frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                member.Plan,
                AnimationStartedAtTimestamp,
                _plan.DurationMilliseconds,
                now);
            return frame.Visible &&
                frame.IsHitTestVisible &&
                !frame.InteractiveBounds.IsEmpty &&
                EdgeCapsuleGeometry.Contains(frame.InteractiveBounds, point);
        });
    }

    private long AnimationStartedAtTimestamp =>
        Volatile.Read(ref _animationStartedAtTimestamp) is var started && started > 0
            ? started
            : Stopwatch.GetTimestamp();

    private void OnSampleTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _finishing)
        {
            return;
        }
        // Pointer ownership follows logical compositor geometry, not the already-positioned real
        // endpoints. This timer is input policy only and is not an animation clock.
        foreach (var member in _members)
        {
            member.Window.InvalidateEdgeCapsuleQueueProxyPointer();
        }
    }

    private void OnCompletionTimerTick(object? sender, EventArgs e)
    {
        _completionTimer.Stop();
        CompleteNow(_completionRetrySuccess);
    }

    public bool TryReleaseForHandoff()
    {
        if (_disposed || _sourcesReleased)
        {
            return _sourcesReleased;
        }
        if (_coverLost)
        {
            return ReleaseAfterCoverLoss();
        }

        var restored = new List<IntPtr>(_cloakedRealSourceHandles.Count);
        var allRestored = true;
        foreach (var handle in _cloakedRealSourceHandles)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                continue;
            }
            if (WindowNative.TrySetWindowCloaked(handle, cloaked: false))
            {
                restored.Add(handle);
            }
            else
            {
                allRestored = false;
            }
        }
        if (!allRestored)
        {
            if (_coverLost)
            {
                // The output vanished during normal handoff. Never hide an already-restored real
                // source again behind a cover that no longer exists; the emergency retry will
                // continue with the exact handles that are still app-cloaked.
                WindowNative.FlushDesktopComposition();
                return false;
            }
            foreach (var handle in restored)
            {
                if (WindowNative.IsWindowHandleAlive(handle))
                {
                    _ = WindowNative.TrySetWindowCloaked(handle, cloaked: true);
                }
            }
            WindowNative.FlushDesktopComposition();
            return false;
        }

        // Every captured source is visible now and has been verified uncloaked. From this point on
        // never cloak it again: a device-loss after the destructive root-detach commit must degrade
        // to a visible real endpoint, not to an empty proxy plus hidden real windows.
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        try
        {
            // Submit uncloaking and cover removal without a DwmFlush between them; flushing while
            // both are visible creates a guaranteed double-composited handoff frame.
            _target.SetRoot(null!).CheckError();
            _device.Commit().CheckError();
            _device.WaitForCommitCompletion().CheckError();
            WindowNative.FlushDesktopComposition();
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue proxy release failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            // The real endpoints are already visible. Teardown is now best-effort and may leave a
            // hidden native resource, but must never restore a cover over successfully revealed UI.
        }
        _window.Hide();
        return true;
    }

    public bool ReleaseAfterCoverLoss()
    {
        if (_disposed || _sourcesReleased)
        {
            return _sourcesReleased;
        }

        // There is no visible cover left to preserve. Restore every exact attempted source before
        // doing any endpoint/layout work; verification failures are retried, but cannot justify
        // keeping real UI hidden behind an output that no longer exists.
        if (!TryRestoreSourcesAfterCoverLoss())
        {
            return false;
        }
        FinalizeReleaseAfterCoverLoss();
        return true;
    }

    private bool TryRestoreSourcesAfterCoverLoss()
    {
        var allRestored = true;
        foreach (var handle in _cloakedRealSourceHandles)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                continue;
            }
            var restored = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (WindowNative.TrySetWindowCloaked(handle, cloaked: false))
                {
                    restored = true;
                    break;
                }
            }
            allRestored &= restored;
        }
        WindowNative.FlushDesktopComposition();
        return allRestored;
    }

    private void FinalizeReleaseAfterCoverLoss()
    {
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        try { _target.SetRoot(null!).CheckError(); } catch { }
        try { _device.Commit().CheckError(); } catch { }
        _window.Hide();
        WindowNative.FlushDesktopComposition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (!_sourcesReleased && !TryReleaseForHandoff())
        {
            return;
        }
        DisposeCore();
    }

    public void ForceDisposeForShutdown()
    {
        if (_disposed)
        {
            return;
        }
        _ = TryReleaseForHandoff();
        DisposeCore();
    }

    private void DisposeCore()
    {
        _disposed = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
        try
        {
            foreach (var visual in _visuals)
            {
                try
                {
                    visual.Dispose();
                }
                catch
                {
                }
            }
            _visuals.Clear();
            try { _root.Dispose(); } catch { }
            try { _target.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
        }
        finally
        {
            try { _window.Dispose(); } catch { }
            foreach (var member in _members)
            {
                try { member.SnapshotHost?.Dispose(); } catch { }
            }
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=dispose session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"released={_sourcesReleased}");
#endif
    }

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice2(
        IntPtr renderingDevice,
        ref Guid iid,
        out IntPtr dcompositionDevice);
}
