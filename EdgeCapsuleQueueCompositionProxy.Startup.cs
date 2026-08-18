using System.Diagnostics;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private bool PrepareAndStart()
    {
#if DEBUG
        var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var snapshotLayers =
            new Dictionary<string, VisualState>(StringComparer.Ordinal);
        var targetSurfaces =
            new Dictionary<string, IUnknown>(StringComparer.Ordinal);
        try
        {
            // The first generation may need to mutate a compact real HWND into its native endpoint.
            // Wrap that HWND before mutation; the wrapper follows the live endpoint and is inserted
            // below an exact 1:1 start cover only after WPF has rendered the final layout.
            foreach (var member in _members.Where(member =>
                         member.Plan.RequiresStartSnapshot))
            {
                _device.CreateSurfaceFromHwnd(
                    member.SourceHandle,
                    out var targetSurface).CheckError();
                targetSurfaces.Add(
                    member.Plan.PaperId,
                    targetSurface);
            }

            foreach (var member in _members)
            {
                var reference =
                    _visuals.Count == 0
                        ? null
                        : _visuals[^1].Visual;
                switch (member.Plan.Role)
                {
                    case EdgeCapsuleQueueProxyMemberRole.MovingSource:
                        _ = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.MovingSource,
                            member.SourceHandle,
                            member.Plan.Source.Bounds,
                            member.Plan.Start.Bounds,
                            member.Plan.Target.Bounds,
                            EdgeCapsuleQueueProxyGeometry.FullClip(
                                member.Plan.Source.Bounds),
                            EdgeCapsuleQueueProxyGeometry.FullClip(
                                member.Plan.Source.Bounds),
                            1,
                            1,
                            reference);
                        break;

                    case EdgeCapsuleQueueProxyMemberRole.RevealTarget:
                    {
                        var startSurfaceBounds =
                            EdgeCapsuleQueueProxyGeometry
                                .PositionSurfaceForVisibleBounds(
                                    member.Plan.Target.Bounds,
                                    member.Plan.Start.Bounds,
                                    member.Plan.Target.Edge);
                        _ = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.RevealTarget,
                            member.SourceHandle,
                            member.Plan.Target.Bounds,
                            startSurfaceBounds,
                            member.Plan.Target.Bounds,
                            EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                                startSurfaceBounds,
                                member.Plan.Start.Bounds,
                                member.Plan.Target.Edge,
                                member.Plan.Target.DpiScaleX,
                                member.Plan.Target.DpiScaleY),
                            EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                                member.Plan.Target.Bounds,
                                member.Plan.Target.Bounds,
                                member.Plan.Target.Edge,
                                member.Plan.Target.DpiScaleX,
                                member.Plan.Target.DpiScaleY),
                            1,
                            1,
                            reference);
                        break;
                    }

                    case EdgeCapsuleQueueProxyMemberRole.ConcealSource:
                    {
                        var startSurfaceBounds =
                            EdgeCapsuleQueueProxyGeometry
                                .PositionSurfaceForVisibleBounds(
                                    member.Plan.Source.Bounds,
                                    member.Plan.Start.Bounds,
                                    member.Plan.Source.Edge);
                        var targetSurfaceBounds =
                            EdgeCapsuleQueueProxyGeometry
                                .PositionSurfaceForVisibleBounds(
                                    member.Plan.Source.Bounds,
                                    member.Plan.Target.Bounds,
                                    member.Plan.Source.Edge);
                        _ = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.ConcealSource,
                            member.SourceHandle,
                            member.Plan.Source.Bounds,
                            startSurfaceBounds,
                            targetSurfaceBounds,
                            EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                                startSurfaceBounds,
                                member.Plan.Start.Bounds,
                                member.Plan.Source.Edge,
                                member.Plan.Source.DpiScaleX,
                                member.Plan.Source.DpiScaleY),
                            EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                                targetSurfaceBounds,
                                member.Plan.Target.Bounds,
                                member.Plan.Source.Edge,
                                member.Plan.Source.DpiScaleX,
                                member.Plan.Source.DpiScaleY),
                            1,
                            1,
                            reference);
                        break;
                    }

                    case EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot:
                    {
                        var snapshotHost = member.SnapshotHost;
                        if (snapshotHost == null ||
                            snapshotHost.Handle == IntPtr.Zero)
                        {
                            return false;
                        }

                        var snapshotSurfaceBounds =
                            EdgeCapsuleQueueProxyGeometry
                                .PositionSurfaceForVisibleBounds(
                                    member.Plan.Source.Bounds,
                                    member.Plan.Start.Bounds,
                                    member.Plan.Source.Edge);
                        var snapshot = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.StartSnapshot,
                            snapshotHost.Handle,
                            member.Plan.Source.Bounds,
                            snapshotSurfaceBounds,
                            snapshotSurfaceBounds,
                            EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                                snapshotSurfaceBounds,
                                member.Plan.Start.Bounds,
                                member.Plan.Source.Edge,
                                member.Plan.Source.DpiScaleX,
                                member.Plan.Source.DpiScaleY),
                            EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                                snapshotSurfaceBounds,
                                member.Plan.Start.Bounds,
                                member.Plan.Source.Edge,
                                member.Plan.Source.DpiScaleX,
                                member.Plan.Source.DpiScaleY),
                            1,
                            0,
                            reference);
                        snapshotLayers.Add(
                            member.Plan.PaperId,
                            snapshot);
                        break;
                    }

                    default:
                        return false;
                }
            }

            // Collect only sources that are not already retained by the predecessor. Snapshot
            // hosts stay cloaked for their whole lifetime; CreateSurfaceFromHwnd explicitly
            // supports composing a cloaked layered HWND, so publishing them never needs another
            // uncloak/re-cloak desktop frame.
            var cloakChanges =
                new List<WindowNative.WindowCloakChange>();
            foreach (var member in _members)
            {
                var inherited =
                    _predecessor?.RetainsSource(member.Window) == true;
                if (!inherited &&
                    _cloakedRealSourceHandles.Add(member.SourceHandle))
                {
                    cloakChanges.Add(new WindowNative.WindowCloakChange(
                        member.SourceHandle,
                        Cloaked: true,
                        RollbackCloaked: false));
                }
            }

            // Root replacement and newly-owned source cloaks share one desktop boundary. The old
            // code synchronously waited for Commit and then immediately crossed DWM again for the
            // cloaks, leaving every A-to-B start frame static for an extra composition interval.
            // DwmFlush publishes all queued DirectX updates from this process; keep the predecessor
            // alive until that boundary has completed, then transfer ownership and retire it.
            _target.SetRoot(_root).CheckError();
            _device.Commit().CheckError();
            _targetRootInstalled = true;

            // The initial generation must place/show the prewarmed output. A successor already owns
            // this exact output HWND and bounds; another SetWindowPos/Show/DwmFlush would only add a
            // desktop-composition stall and another opportunity for a torn handoff.
            if (_predecessor == null)
            {
                if (!_window.Show(_outputBounds, _plan.Topmost))
                {
                    return false;
                }
            }

            var rootPublished = cloakChanges.Count > 0
                ? WindowNative.TrySetWindowCloakedBatch(cloakChanges)
                : WindowNative.TryFlushDesktopComposition();
            if (!rootPublished || _coverLost)
            {
                return false;
            }

            if (!_host.Promote(this, _predecessor) ||
                !_coverReady(this))
            {
                return false;
            }
            _coverPublished = true;

            _realEndpointMutationStarted = true;
#if DEBUG
            var endpointStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var endpointMembers = _members
                .Where(member => !member.Plan.DefersRealEndpoint)
                .ToArray();
            var endpointReady = true;
            foreach (var member in endpointMembers)
            {
                endpointReady &= member.Window
                    .ApplyEdgeCapsuleQueueProxyEndpoint(
                        member.Plan.Target);
            }

            if (endpointReady)
            {
                foreach (var member in endpointMembers.Where(member =>
                             member.Plan.UsesTargetSurface))
                {
                    endpointReady &= member.Window
                        .PrepareEdgeCapsuleQueueProxyEndpointLayoutForHandoff();
                }
            }

            var nativeRevealMembers = endpointMembers
                .Where(member => member.Plan.UsesTargetSurface)
                .ToArray();
            if (endpointReady && nativeRevealMembers.Length > 0)
            {
                try
                {
                    // Every endpoint tree has already completed Measure/Arrange. Submit them in one
                    // Render turn, then let the animation commit and this WPF publication share the
                    // same desktop boundary below. Flushing before ConfigureAnimations held the
                    // exact start frame for another 1-2 high-refresh frames.
                    _members[0].Window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.Render);
                }
                catch
                {
                    endpointReady = false;
                }
            }
            if (!endpointReady)
            {
                return false;
            }

            // Insert the native endpoint below its 1:1 start cover. The bitmap never changes size;
            // the animation commit replaces it atomically so its old outline cannot remain visible
            // inside the expanding endpoint.
            foreach (var member in _members.Where(member =>
                         member.Plan.RequiresStartSnapshot))
            {
                if (!targetSurfaces.Remove(
                        member.Plan.PaperId,
                        out var targetSurface) ||
                    !snapshotLayers.TryGetValue(
                        member.Plan.PaperId,
                        out var snapshotLayer))
                {
                    return false;
                }

                var startSurfaceBounds =
                    EdgeCapsuleQueueProxyGeometry
                        .PositionSurfaceForVisibleBounds(
                            member.Plan.Target.Bounds,
                            member.Plan.Start.Bounds,
                            member.Plan.Target.Edge);
                _ = AddVisual(
                    member,
                    EdgeCapsuleQueueProxyVisualLayer.RevealTarget,
                    member.SourceHandle,
                    member.Plan.Target.Bounds,
                    startSurfaceBounds,
                    member.Plan.Target.Bounds,
                    EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                        startSurfaceBounds,
                        member.Plan.Start.Bounds,
                        member.Plan.Target.Edge,
                        member.Plan.Target.DpiScaleX,
                        member.Plan.Target.DpiScaleY),
                    EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                        member.Plan.Target.Bounds,
                        member.Plan.Target.Bounds,
                        member.Plan.Target.Edge,
                        member.Plan.Target.DpiScaleX,
                        member.Plan.Target.DpiScaleY),
                    1,
                    1,
                    snapshotLayer.Visual,
                    insertAbove: false,
                    existingSurface: targetSurface);
            }

            // Animation commit is asynchronous. All WPF layout and source preparation completed
            // while the exact start cover was already on screen. Give DirectComposition the same
            // QPC timestamp used by logical hit-testing so commit pickup latency cannot put the GPU
            // animation on a different frame from the controller clock.
            _animationStartedAtTimestamp = Stopwatch.GetTimestamp();
            ConfigureAnimations(_animationStartedAtTimestamp);
            _device.Commit().CheckError();
            if (nativeRevealMembers.Length > 0)
            {
                try
                {
                    WindowNative.FlushDesktopComposition();
                    foreach (var member in nativeRevealMembers)
                    {
                        endpointReady &= member.Window
                            .VerifyEdgeCapsuleQueueProxyEndpoint(
                                member.Plan.Target);
                    }
                }
                catch
                {
                    endpointReady = false;
                }
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=prepare session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"members={endpointMembers.Length} " +
                $"nativeReveal={nativeRevealMembers.Length} " +
                $"ready={endpointReady} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointStartedAt):F3}");
#endif
            if (!endpointReady)
            {
                return false;
            }
            _sampleTimer.Start();
            var elapsedSinceAnimationStart =
                Stopwatch.GetElapsedTime(
                    _animationStartedAtTimestamp,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            _completionTimer.Interval =
                TimeSpan.FromMilliseconds(
                    Math.Max(
                        1,
                        _plan.DurationMilliseconds +
                        CompletionGuardMilliseconds -
                        elapsedSinceAnimationStart));
            _completionTimer.Start();
#if DEBUG
            var outputPixels =
                (long)_outputBounds.Width * _outputBounds.Height;
            var wrappedPixels = _visuals.Sum(state =>
                (long)state.SourceBounds.Width *
                state.SourceBounds.Height);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=start session={_sessionOrdinal} " +
                $"cold={IsColdSession} successor={_predecessor != null} " +
                $"queue={_plan.QueueKey} members={_members.Count} " +
                $"durationMs={_plan.DurationMilliseconds} " +
                $"visualMode=native-clip output={_outputBounds.Left},{_outputBounds.Top}," +
                $"{_outputBounds.Width}x{_outputBounds.Height} " +
                $"prepareMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"resource.proxy session={_sessionOrdinal} cold={IsColdSession} " +
                $"queue={_plan.QueueKey} scope=geometry-estimate excludesDwmGpu=true " +
                $"outputPixels={outputPixels} wrappedPixels={wrappedPixels} " +
                $"wrappedRgbaEstimateMiB={wrappedPixels * 4 / (1024.0 * 1024.0):F3} " +
                $"snapshotHosts={_members.Count(member => member.SnapshotHost != null)}");
#endif
            return true;
        }
        finally
        {
            foreach (var surface in targetSurfaces.Values)
            {
                try { surface.Dispose(); } catch { }
            }
        }
    }
}
