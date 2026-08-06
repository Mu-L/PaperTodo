using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One animation-frame scheduler per UI dispatcher. Presenters still own their transitions and
/// reconcile pipelines; the shared scheduler only batches frame advances and cursor sampling
/// on WPF's actual composition frames.
/// </summary>
internal sealed class EdgeCapsuleFrameScheduler
{
    private static readonly ConditionalWeakTable<Dispatcher, EdgeCapsuleFrameScheduler> Schedulers = new();

    private readonly Dispatcher _dispatcher;
    private readonly List<EdgeCapsulePresenter> _presenters = new();
    private bool _renderingSubscribed;
    private bool _isTicking;

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(dispatcher, static key => new EdgeCapsuleFrameScheduler(key));

    public void Activate(EdgeCapsulePresenter presenter)
    {
        _dispatcher.VerifyAccess();
        if (!_presenters.Contains(presenter))
        {
            _presenters.Add(presenter);
        }
        if (!_renderingSubscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
        }
    }

    public void Deactivate(EdgeCapsulePresenter presenter)
    {
        _dispatcher.VerifyAccess();
        // Removing from the list while another presenter's reconcile is running would invalidate
        // the backwards iteration. The post-tick sweep observes the presenter's inactive flag.
        if (_isTicking)
        {
            return;
        }

        _presenters.Remove(presenter);
        StopWhenEmpty();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // CompositionTarget.Rendering can be nested when presentation work pumps WPF messages.
        // A nested tick must never observe or mutate the list owned by the outer tick.
        if (!_dispatcher.CheckAccess() || _isTicking)
        {
            return;
        }

        _isTicking = true;
        try
        {
            var initialCount = _presenters.Count;
            var pointer = WindowNative.TryGetCursorScreenPosition(out var currentPointer)
                ? currentPointer
                : (DeviceScreenPoint?)null;

            // Iterate backwards so a completing presenter can be removed without a per-frame
            // snapshot allocation. Presenters activated during this tick start on the next one.
            for (var index = initialCount - 1; index >= 0; index--)
            {
                var presenter = _presenters[index];
                if (!presenter.AdvanceSharedFrame(this, pointer))
                {
                    _presenters.RemoveAt(index);
                }
            }

            // Deactivate is intentionally deferred while ticking. Remove all presenters that
            // stopped themselves during reconcile before the next composition frame.
            for (var index = _presenters.Count - 1; index >= 0; index--)
            {
                if (!_presenters[index].UsesSharedFrameScheduler(this))
                {
                    _presenters.RemoveAt(index);
                }
            }
        }
        finally
        {
            _isTicking = false;
            StopWhenEmpty();
        }
    }

    private void StopWhenEmpty()
    {
        if (_presenters.Count == 0 && _renderingSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingSubscribed = false;
        }
    }
}
