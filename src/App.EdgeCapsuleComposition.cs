using System.Windows.Threading;

namespace PaperTodo;

public partial class App
{
    public App()
    {
        // Pay known DComp publication and WPF HWND first-use costs at ApplicationIdle so the
        // first real edge-preview transaction does not carry them on the interaction path.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() =>
            {
                EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(Dispatcher);
            }));
    }
}
