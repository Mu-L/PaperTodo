using System.Windows.Threading;

namespace PaperTodo;

public partial class App
{
    public App()
    {
        // Device creation is independent from any queue plan. Warm it after startup work drains so
        // the first user hover does not pay DCompositionCreateDevice2 on the animation path.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() =>
            {
                EdgeCapsuleQueueCompositionProxy.Prewarm(Dispatcher);
                EdgeCapsuleProxySnapshotHost.Prewarm(Dispatcher);
            }));
    }
}
