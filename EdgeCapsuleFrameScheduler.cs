using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One animation-frame scheduler per UI dispatcher. Presenters still own their transitions and
/// reconcile pipelines; the shared scheduler only batches frame advances plus cursor/time sampling
/// on WPF's actual composition frames.
/// </summary>
internal sealed class EdgeCapsuleFrameScheduler
{
    private static readonly ConditionalWeakTable<Dispatcher, EdgeCapsuleFrameScheduler> Schedulers = new();

    private readonly Dispatcher _dispatcher;
    private readonly List<EdgeCapsulePresenter> _presenters = new();
    private bool _renderingSubscribed;
    private bool _isTicking;
    private bool _deferRenderingForLoadedBatch;
    private int _loadedBatchGeneration;

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(dispatcher, static key => new EdgeCapsuleFrameScheduler(key));

    /// <summary>
    /// Prevent a Render-priority composition callback from advancing one presenter while sibling
    /// presenters are still waiting in the same Loaded-priority reconcile batch. Every caller
    /// appends a release marker after its own queued reconcile; only the newest marker can release
    /// the barrier, so interleaved queue members still begin from the same composition frame.
    /// </summary>
    public void DeferRenderingUntilLoadedBatchDrains()
    {
        _dispatcher.VerifyAccess();
        _deferRenderingForLoadedBatch = true;
        var generation = ++_loadedBatchGeneration;
        _dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (generation == _loadedBatchGeneration)
                {
                    _deferRenderingForLoadedBatch = false;
                }
            }),
            DispatcherPriority.Loaded);
    }

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
        // A nested tick must never observe or mutate the list owned by the outer tick. Render has
        // higher dispatcher priority than Loaded, so also hold the frame while a cross-window
        // Loaded batch is still preparing sibling targets.
        if (!_dispatcher.CheckAccess() ||
            _isTicking ||
            _deferRenderingForLoadedBatch)
        {
            return;
        }

        _isTicking = true;
        try
        {
            var initialCount = _presenters.Count;
            var frameTimestamp = Stopwatch.GetTimestamp();
            var pointer = WindowNative.TryGetCursorScreenPosition(out var currentPointer)
                ? currentPointer
                : (DeviceScreenPoint?)null;
            using var nativeBoundsBatch =
                WindowNative.BeginWindowDeviceBoundsBatch(initialCount);

            // Iterate backwards without a per-frame snapshot allocation. Deactivation is deferred
            // until the native batch commits; presenters activated during this tick start next time.
            for (var index = initialCount - 1; index >= 0; index--)
            {
                var presenter = _presenters[index];
                _ = presenter.AdvanceSharedFrame(
                    this,
                    pointer,
                    frameTimestamp);
            }

            if (!nativeBoundsBatch.Commit())
            {
                for (var index = initialCount - 1; index >= 0; index--)
                {
                    _presenters[index].RetrySharedFrameAfterNativeBatchFailure(this);
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
