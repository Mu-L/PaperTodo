namespace PaperTodo;

public sealed partial class AppController
{
    internal bool TryFreezeEdgeCapsuleQueueProxyDeferredEndpointSource(
        PaperWindow window)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var proxy))
        {
            return proxy.TryFreezeDeferredEndpointSource(window);
        }
        return true;
    }
}
