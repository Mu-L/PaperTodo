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
        var startLayers = new Dictionary<string, VisualState>(StringComparer.Ordinal);
        var openingEndpointSurfaces = new Dictionary<string, IUnknown>(StringComparer.Ordinal);
        try
        {
            // Wrap opening endpoints while their real HWNDs are still visible, but do not attach
            // those wrappers until the HWNDs have rendered at the target size under the start cover.
            foreach (var member in _members.Where(member =>
                         member.Plan.Role == EdgeCapsuleQueueProxyMemberRole.OpeningPreview))
            {
                _device.CreateSurfaceFromHwnd(
                    member.SourceHandle,
                    out var endpointSurface).CheckError();
                openingEndpointSurfaces.Add(member.Plan.PaperId, endpointSurface);
            }

            foreach (var member in _members)
            {
                var sourceHandle = member.SnapshotHost?.Handle ?? member.SourceHandle;
                var state = AddVisual(
                    member,
                    sourceHandle,
                    member.Plan.Start.Bounds,
                    member.Plan.Start.Bounds,
                    initialOpacity: 1,
                    endpointLayer: false,
                    referenceVisual: _visuals.Count == 0 ? null : _visuals[^1].Visual);
                startLayers[member.Plan.PaperId] = state;
            }

            // Commit a complete start-state cover before any real endpoint changes.
            _device.Commit().CheckError();
            _device.WaitForCommitCompletion().CheckError();
            if (!_window.Show(_outputBounds, _plan.Topmost))
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
                _cloakedRealSourceHandles.Add(member.SourceHandle);
                if (!WindowNative.TrySetWindowCloaked(member.SourceHandle, cloaked: true))
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
                // Translation-only peers keep the same WPF surface; moving their cloaked HWND to
                // the endpoint does not require another layout/render/DwmFlush barrier. Only an
                // opening preview has changed source size/content and must drain WPF before its
                // endpoint wrapper is attached.
                foreach (var member in endpointMembers.Where(member =>
                             member.Plan.Role ==
                                 EdgeCapsuleQueueProxyMemberRole.OpeningPreview))
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

            foreach (var member in _members.Where(member =>
                         member.Plan.Role == EdgeCapsuleQueueProxyMemberRole.OpeningPreview))
            {
                if (!openingEndpointSurfaces.Remove(
                        member.Plan.PaperId,
                        out var endpointSurface))
                {
                    return false;
                }
                var startLayer = startLayers[member.Plan.PaperId];
                _ = AddVisual(
                    member,
                    member.SourceHandle,
                    member.Plan.Target.Bounds,
                    member.Plan.Start.Bounds,
                    initialOpacity: 0,
                    endpointLayer: true,
                    referenceVisual: startLayer.Visual,
                    existingSurface: endpointSurface);
            }

            foreach (var state in _visuals)
            {
                ConfigureAnimations(state);
            }

            // The hot animation commit has no synchronous completion wait. The completion timer
            // retains a 34 ms safety margin before the endpoint handoff.
            _device.Commit().CheckError();
            _animationStartedAtTimestamp = Stopwatch.GetTimestamp();
            _sampleTimer.Start();
            _completionTimer.Start();
#if DEBUG
            var outputPixels = (long)_outputBounds.Width * _outputBounds.Height;
            var wrappedPixels = _visuals.Sum(state =>
                (long)state.SourceBounds.Width * state.SourceBounds.Height);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=start session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"members={_members.Count} durationMs={_plan.DurationMilliseconds} " +
                $"output={_outputBounds.Left},{_outputBounds.Top}," +
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
            foreach (var surface in openingEndpointSurfaces.Values)
            {
                try { surface.Dispose(); } catch { }
            }
        }
    }
}
