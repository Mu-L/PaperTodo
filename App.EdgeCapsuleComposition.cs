using System.Windows.Threading;

namespace PaperTodo;

public partial class App
{
    public App()
    {
        // Pay first-use composition costs at ApplicationIdle, after the real edge hosts exist.
        // The lightweight probe warms WPF/Win32/DComp primitives; the product-host probe then
        // wraps those already-visible real host HWNDs on the exact spare QueueHost target without
        // moving or cloaking any paper window.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() =>
            {
                EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(Dispatcher);
                EdgeCapsuleQueueCompositionProxy.PrewarmProductHostAssembly(Dispatcher);
            }));
    }
}
