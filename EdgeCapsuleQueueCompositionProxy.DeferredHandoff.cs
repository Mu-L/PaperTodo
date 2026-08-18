using System.Diagnostics;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private readonly HashSet<PaperWindow> _frozenDeferredEndpointSources = new();

    /// <summary>
    /// ConcealSource renders from the live preview HWND for the whole shrink animation. Resizing
    /// that HWND to the compact endpoint before authority swap changes the surface dimensions under
    /// the already-final DComp clip (for example 163px source with a 77..163px compact clip becomes
    /// an 86px surface with only 9px of that clip still backed by pixels). Freeze the complete live
    /// source into a same-size 1:1 snapshot first, then replace only the visual content. Geometry,
    /// clip and screen position do not change, but the real HWND is now free to resize underneath.
    /// </summary>
    internal bool TryFreezeDeferredEndpointSource(PaperWindow window)
    {
        if (_disposed ||
            _coverLost ||
            _sourcesReleased ||
            !ReferenceEquals(_host.Current, this))
        {
            return false;
        }
        if (_frozenDeferredEndpointSources.Contains(window))
        {
            return true;
        }

        var memberIndex = -1;
        for (var index = 0; index < _members.Count; index++)
        {
            if (ReferenceEquals(_members[index].Window, window))
            {
                memberIndex = index;
                break;
            }
        }
        if (memberIndex < 0)
        {
            return false;
        }

        var member = _members[memberIndex];
        if (!member.Plan.DefersRealEndpoint)
        {
            return true;
        }
        if (_members is not IList<EdgeCapsuleQueueCompositionProxyMember> mutableMembers)
        {
            return false;
        }

        var liveVisual = _visuals.LastOrDefault(state =>
            ReferenceEquals(state.Member.Window, window) &&
            state.Layer == EdgeCapsuleQueueProxyVisualLayer.ConcealSource);
        if (liveVisual == null)
        {
            return false;
        }

        EdgeCapsuleProxySnapshotHost? snapshotHost = null;
        VisualState? frozenVisual = null;
#if DEBUG
        var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var captureMilliseconds = 0.0;
        var hostMilliseconds = 0.0;
        var swapMilliseconds = 0.0;
#endif
        try
        {
#if DEBUG
            var captureStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var snapshot = window.CaptureEdgeCapsuleQueueProxySnapshot(
                member.Plan.Source);
#if DEBUG
            captureMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                captureStartedAt);
            var hostStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            snapshotHost = snapshot == null
                ? null
                : EdgeCapsuleProxySnapshotHost.TryCreate(
                    snapshot,
                    member.Plan.Source);
#if DEBUG
            hostMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                hostStartedAt);
#endif
            if (snapshotHost == null || snapshotHost.Handle == IntPtr.Zero)
            {
                return false;
            }

            var targetSurfaceBounds =
                EdgeCapsuleQueueProxyGeometry.PositionSurfaceForVisibleBounds(
                    member.Plan.Source.Bounds,
                    member.Plan.Target.Bounds,
                    member.Plan.Source.Edge);
            if (targetSurfaceBounds.IsEmpty)
            {
                return false;
            }
            var targetClip =
                EdgeCapsuleQueueProxyGeometry.RoundedBodyClipForVisibleBounds(
                    targetSurfaceBounds,
                    member.Plan.Target.Bounds,
                    member.Plan.Source.Edge,
                    member.Plan.Source.DpiScaleX,
                    member.Plan.Source.DpiScaleY);
            if (targetClip.IsEmpty)
            {
                return false;
            }

            var frozenMember = member with { SnapshotHost = snapshotHost };
            frozenVisual = AddVisual(
                frozenMember,
                EdgeCapsuleQueueProxyVisualLayer.ConcealSource,
                snapshotHost.Handle,
                member.Plan.Source.Bounds,
                targetSurfaceBounds,
                targetSurfaceBounds,
                targetClip,
                targetClip,
                1,
                1,
                liveVisual.Visual,
                insertAbove: true);

#if DEBUG
            var swapStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            // The new snapshot and the old live visual represent the exact same final proxy pixels.
            // Publish the content-identity swap before the real HWND can be resized.
            liveVisual.Effect.SetOpacity(0).CheckError();
            _device.Commit().CheckError();
            if (!WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The deferred endpoint freeze did not reach the desktop boundary.");
            }
#if DEBUG
            swapMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                swapStartedAt);
#endif

            mutableMembers[memberIndex] = frozenMember;
            _frozenDeferredEndpointSources.Add(window);
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.handoff phase=freeze-deferred session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(member.Plan.PaperId)} " +
                $"source={member.Plan.Source.Bounds.Width}x{member.Plan.Source.Bounds.Height} " +
                $"target={member.Plan.Target.Bounds.Width}x{member.Plan.Target.Bounds.Height} " +
                $"captureMs={captureMilliseconds:F3} hostMs={hostMilliseconds:F3} " +
                $"swapMs={swapMilliseconds:F3} totalMs=" +
                $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
#endif
            snapshotHost = null;
            return true;
        }
        catch (Exception ex)
        {
            var rollbackSucceeded = true;
            try
            {
                liveVisual.Effect.SetOpacity(1).CheckError();
                if (frozenVisual != null)
                {
                    frozenVisual.Effect.SetOpacity(0).CheckError();
                }
                _device.Commit().CheckError();
                rollbackSucceeded = WindowNative.TryFlushDesktopComposition();
            }
            catch
            {
                rollbackSucceeded = false;
            }
            if (!rollbackSucceeded)
            {
                _coverLost = true;
            }
            Trace.TraceError(
                "Edge capsule deferred handoff freeze failed. Queue={0}; Session={1}; Paper={2}; Exception={3}",
                _plan.QueueKey,
                _sessionOrdinal,
                member.Plan.PaperId,
                ex);
            return false;
        }
        finally
        {
            snapshotHost?.Dispose();
        }
    }
}
