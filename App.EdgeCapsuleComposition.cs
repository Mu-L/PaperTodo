using System.Windows.Threading;

namespace PaperTodo;

public partial class App
{
    public App()
    {
        // Warm the cheap DComp/native publication path after startup work drains so the first real
        // preview does not pay device/target/visual/commit/show/DwmFlush/cloak first-use costs. WPF
        // is deliberately left cold here; existing host.apply timings remain the unbiased probe for
        // deciding whether a separate WPF warm-up is worth its startup and memory cost.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() =>
            {
                EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(Dispatcher);
            }));
    }
}
