namespace PaperTodo;

public sealed partial class AppController
{
    internal bool TryStartEdgeCapsulePointerCompositionProxy(
        PaperWindow window)
    {
        if (IsExiting || window.IsClosed)
        {
            return false;
        }

        var queueKey =
            QueueKey(window.EdgeCapsulePreviewPaper);
        _edgeCapsuleQueueCompositionProxies.TryGetValue(
            queueKey,
            out var predecessor);
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var routed) &&
            !ReferenceEquals(routed, predecessor))
        {
            return false;
        }

        if (predecessor != null &&
            !predecessor.TryHoldForSuccessor())
        {
            return false;
        }

        var pointerMotion = EdgeCapsuleMotion.Animate(
            EdgeCapsuleTransitionReason.Pointer,
            EdgeCapsuleLayout.HorizontalResizeMilliseconds);
        var entriesByWindow =
            new Dictionary<PaperWindow, EdgeCapsuleVisualTransactionEntry>();

        // A successor replaces the predecessor root on the same output HWND. Carry every source
        // still owned by that root into the new transaction; otherwise a stationary peer would
        // vanish while its real HWND remains cloaked. Peers continue toward their current reducer
        // endpoint on the same short compositor clock, while the triggering window owns Pointer.
        if (predecessor != null)
        {
            var peerMotion = EdgeCapsuleMotion.Animate(
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.HorizontalResizeMilliseconds);
            foreach (var member in predecessor.Members)
            {
                if (member.Window.IsClosed)
                {
                    continue;
                }
                entriesByWindow[member.Window] =
                    new EdgeCapsuleVisualTransactionEntry(
                        member.Window,
                        queueKey,
                        ReferenceEquals(member.Window, window)
                            ? pointerMotion
                            : peerMotion,
                        RefreshLayout: false);
            }
        }

        entriesByWindow[window] =
            new EdgeCapsuleVisualTransactionEntry(
                window,
                queueKey,
                pointerMotion,
                RefreshLayout: false);
        var entries = entriesByWindow.Values.ToArray();

        var plan =
            TryCreateEdgeCapsuleQueueProxyPlan(
                entries,
                predecessor);
        if (plan == null)
        {
            predecessor?.CompleteAfterFailedSuccessor(
                success: true);
            return false;
        }

        var started =
            TryStartEdgeCapsuleQueueCompositionProxy(
                plan,
                entries,
                predecessor,
                out var realHostMayHaveChanged);
        if (started &&
            !CommitEdgeCapsuleQueueProxyLogicalEndpoints(
                entries,
                System.Diagnostics.Stopwatch.GetTimestamp()))
        {
            CompleteEdgeCapsuleQueueCompositionProxy(
                queueKey,
                success: false);
            started = false;
            realHostMayHaveChanged = true;
        }

        if (!started &&
            predecessor != null &&
            _edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var current) &&
            ReferenceEquals(current, predecessor))
        {
            predecessor.CompleteAfterFailedSuccessor(
                success: true);
        }
        else if (!started &&
                 realHostMayHaveChanged &&
                 !_edgeCapsuleQueueCompositionProxies
                     .ContainsKey(queueKey))
        {
            _ = CommitEdgeCapsuleQueueProxyLogicalEndpoints(
                entries,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.pointer phase=start-attempt queue={queueKey} " +
            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(window.EdgeCapsulePreviewPaperId)} " +
            $"successor={predecessor != null} members={entries.Length} started={started} " +
            $"realHostChanged={realHostMayHaveChanged}");
#endif
        return started;
    }
}
