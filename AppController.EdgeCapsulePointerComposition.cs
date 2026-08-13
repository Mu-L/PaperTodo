namespace PaperTodo;

public sealed partial class AppController
{
    internal bool TryStartEdgeCapsulePointerCompositionProxy(PaperWindow window)
    {
        if (IsExiting || window.IsClosed)
        {
            return false;
        }

        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        if (_edgeCapsuleQueueCompositionProxies.ContainsKey(queueKey) ||
            _edgeCapsuleQueueCompositionProxyByWindow.ContainsKey(window))
        {
            return false;
        }

        var motion = EdgeCapsuleMotion.Animate(
            EdgeCapsuleTransitionReason.Pointer,
            EdgeCapsuleLayout.HorizontalResizeMilliseconds);
        var entries = new[]
        {
            new EdgeCapsuleVisualTransactionEntry(
                window,
                queueKey,
                motion,
                RefreshLayout: false)
        };
        var plan = TryCreateEdgeCapsuleQueueProxyPlan(entries);
        if (plan == null)
        {
            return false;
        }

        var started = TryStartEdgeCapsuleQueueCompositionProxy(
            plan,
            entries,
            out var realHostMayHaveChanged);
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.pointer phase=start-attempt queue={queueKey} " +
            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(window.EdgeCapsulePreviewPaperId)} " +
            $"started={started} realHostChanged={realHostMayHaveChanged}");
#endif
        return started;
    }
}
