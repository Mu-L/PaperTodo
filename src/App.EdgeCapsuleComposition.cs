using System.Windows.Threading;

namespace PaperTodo;

public partial class App
{
    public App()
    {
        // Pay known DComp publication and WPF HWND first-use costs during startup only when edge
        // browsing is enabled. Users who keep the feature off should not create any prewarm HWNDs
        // or compositor resources just for this path.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() =>
            {
                if (AppController.Current?.State.ExperimentalEdgeCapsuleHoverPreview != true)
                {
                    return;
                }

                EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(Dispatcher);
            }));
    }
}
