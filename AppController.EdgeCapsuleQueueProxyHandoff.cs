namespace PaperTodo;

public sealed partial class AppController
{
    internal bool TryFreezeEdgeCapsuleQueueProxyDeferredEndpointSource(
        PaperWindow window)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var routed))
        {
            return routed.TryFreezeDeferredEndpointSource(window);
        }

        // Handoff safety must not depend on the secondary window index being present. If the queue
        // still has a current compositor owner and it routes this window, fail closed through that
        // owner instead of letting a live ConcealSource HWND resize underneath its DComp surface.
        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        if (_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var current) &&
            current.Routes(window))
        {
            return current.TryFreezeDeferredEndpointSource(window);
        }
        return true;
    }
}
