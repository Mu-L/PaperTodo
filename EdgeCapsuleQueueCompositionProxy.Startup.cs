using System.Diagnostics;
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
                        _ = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.RevealTarget,
                            member.SourceHandle,
                            member.Plan.Target.Bounds,
                            member.Plan.Target.Bounds,
                            member.Plan.Target.Bounds,
                            EdgeCapsuleQueueProxyGeometry.ClipForVisibleBounds(
                                member.Plan.Target.Bounds,
                                member.Plan.Start.Bounds),
                            EdgeCapsuleQueueProxyGeometry.FullClip(
                                member.Plan.Target.Bounds),
                            1,
                            1,
                            reference);
                        break;

                    case EdgeCapsuleQueueProxyMemberRole.ConcealSource:
                        _ = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.ConcealSource,
                            member.SourceHandle,
                            member.Plan.Source.Bounds,
                            member.Plan.Source.Bounds,
                            member.Plan.Source.Bounds,
                            EdgeCapsuleQueueProxyGeometry.ClipForVisibleBounds(
                                member.Plan.Source.Bounds,
                                member.Plan.Start.Bounds),
                            EdgeCapsuleQueueProxyGeometry.ClipForVisibleBounds(
                                member.Plan.Source.Bounds,
                                member.Plan.Target.Bounds),
                            1,
                            1,
                            reference);
                        break;

                    case EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot:
                    {
                        var snapshotHost = member.SnapshotHost;
                        if (snapshotHost == null ||
                            snapshotHost.Handle == IntPtr.Zero)
                        {
                            return false;
                        }

                        var snapshot = AddVisual(
                            member,
                            EdgeCapsuleQueueProxyVisualLayer.StartSnapshot,
                            snapshotHost.Handle,
                            member.Plan.Start.Bounds,
                            member.Plan.Start.Bounds,
                            member.Plan.Start.Bounds,
                            EdgeCapsuleQueueProxyGeometry.FullClip(
                                member.Plan.Start.Bounds),
                            EdgeCapsuleQueueProxyGeometry.FullClip(
                                member.Plan.Start.Bounds),
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

            // For a successor this commit replaces predecessor.Root on the same target HWND. The
            // new root describes exactly the predecessor's sampled frame, so DWM never observes a
            // frame in which the still-cloaked real sources are uncovered.
            _target.SetRoot(_root).CheckError();
            _device.Commit().CheckError();
            _device.WaitForCommitCompletion().CheckError();
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
                WindowNative.FlushDesktopComposition();
            }

            if (_coverLost ||
                !_host.Promote(this, _predecessor) ||
                !_coverReady(this))
            {
                return false;
            }
            _coverPublished = true;

            // The controller callback has transferred predecessor-owned cloak handles. Only newly
            // participating sources need another cloak call. Snapshot hosts are ordinary off-screen
            // HWNDs and are cloaked once DirectComposition owns their exact start pixels.
            var desktopBarrierRequired = false;
            foreach (var member in _members)
            {
                if (_cloakedRealSourceHandles.Add(member.SourceHandle))
                {
                    if (!WindowNative.TrySetWindowCloaked(
                            member.SourceHandle,
                            cloaked: true))
                    {
                        _cloakedRealSourceHandles.Remove(
                            member.SourceHandle);
                        return false;
                    }
                    desktopBarrierRequired = true;
                }

                if (member.SnapshotHost != null)
                {
                    if (!member.SnapshotHost.TrySetCloaked(
                            cloaked: true))
                    {
                        return false;
                    }
                    desktopBarrierRequired = true;
                }
            }
            if (desktopBarrierRequired)
            {
                WindowNative.FlushDesktopComposition();
            }
            if (_coverLost)
            {
                return false;
            }

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
                        .PrepareEdgeCapsuleQueueProxyEndpointForHandoff();
                }
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=prepare session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"members={endpointMembers.Length} " +
                $"nativeReveal={endpointMembers.Count(member => member.Plan.UsesTargetSurface)} " +
                $"ready={endpointReady} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointStartedAt):F3}");
#endif
            if (!endpointReady)
            {
                return false;
            }

            // Insert the native endpoint below its 1:1 start cover. The bitmap never changes size;
            // it fades quickly while RectangleClip reveals the already-rendered native endpoint.
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

                _ = AddVisual(
                    member,
                    EdgeCapsuleQueueProxyVisualLayer.RevealTarget,
                    member.SourceHandle,
                    member.Plan.Target.Bounds,
                    member.Plan.Target.Bounds,
                    member.Plan.Target.Bounds,
                    EdgeCapsuleQueueProxyGeometry.ClipForVisibleBounds(
                        member.Plan.Target.Bounds,
                        member.Plan.Start.Bounds),
                    EdgeCapsuleQueueProxyGeometry.FullClip(
                        member.Plan.Target.Bounds),
                    1,
                    1,
                    snapshotLayer.Visual,
                    insertAbove: false,
                    existingSurface: targetSurface);
            }

            ConfigureAnimations();

            // Animation commit is asynchronous. All WPF layout and source preparation completed
            // while the exact start cover was already on screen.
            _animationStartedAtTimestamp = Stopwatch.GetTimestamp();
            _device.Commit().CheckError();
            _sampleTimer.Start();
            _completionTimer.Interval =
                TimeSpan.FromMilliseconds(
                    _plan.DurationMilliseconds + 34);
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
