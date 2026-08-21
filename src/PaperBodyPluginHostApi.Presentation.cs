using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi
{
    public string PaperId
    {
        get
        {
            EnsureUsable();
            return _hostPaperId;
        }
    }

    public void Show(bool activate = true) =>
        QueuePresentation(() =>
            _controller.TryShowPluginHostPaper(_hostPaperId, _providerId, activate));

    public void Hide() =>
        QueuePresentation(() =>
            _controller.TryHidePluginHostPaper(_hostPaperId, _providerId));

    public void ToggleVisibility(bool activate = true) =>
        QueuePresentation(() =>
            _controller.TryTogglePluginHostPaperVisibility(
                _hostPaperId,
                _providerId,
                activate));

    public void Expand(bool activate = true) =>
        QueuePresentation(() =>
            _controller.TryExpandPluginHostPaper(_hostPaperId, _providerId, activate));

    public void Collapse() =>
        QueuePresentation(() =>
            _controller.TryCollapsePluginHostPaper(_hostPaperId, _providerId));

    public void ToggleCollapsed(bool activate = true) =>
        QueuePresentation(() =>
            _controller.TryTogglePluginHostPaperCollapsed(
                _hostPaperId,
                _providerId,
                activate));

    public void Activate() =>
        QueuePresentation(() =>
            _controller.TryActivatePluginHostPaper(_hostPaperId, _providerId));

    private void QueuePresentation(Func<bool> action)
    {
        EnsureUsable();
        if (string.IsNullOrEmpty(_hostPaperId))
        {
            throw Error(
                "host_paper_unavailable",
                "This plugin context is not attached to a paper.");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw Error(
                "host_unavailable",
                "PaperTodo is shutting down.");
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_disposed || !_isSessionCurrent())
                {
                    return;
                }
                _ = action();
            }),
            DispatcherPriority.Background);
    }
}
