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
    private readonly List<Action> _postCommitCallbacks = new();
    private bool _renderingSubscribed;
    private bool _isTicking;
    private bool _acceptingPostCommitCallbacks;
    private int _pendingLoadedReconciles;

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(dispatcher, static key => new EdgeCapsuleFrameScheduler(key));

    /// <summary>
    /// Prevent a Render-priority composition callback from advancing one presenter while sibling
    /// presenters are still waiting in the same Loaded-priority reconcile batch. Counting the
    /// actual queued operations also lets a host-input promotion drain its registration at Send.
    /// </summary>
    public void RegisterLoadedReconcile()
    {
        _dispatcher.VerifyAccess();
        _pendingLoadedReconciles++;
    }

    public void CompleteLoadedReconcile()
    {
        _dispatcher.VerifyAccess();
        if (_pendingLoadedReconciles > 0)
        {
            _pendingLoadedReconciles--;
        }
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

    internal bool TryEnqueuePostCommit(Action callback)
    {
        _dispatcher.VerifyAccess();
        if (!_isTicking || !_acceptingPostCommitCallbacks)
        {
            return false;
        }

        _postCommitCallbacks.Add(callback);
        return true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // CompositionTarget.Rendering can be nested when presentation work pumps WPF messages.
        // A nested tick must never observe or mutate the list owned by the outer tick. Render has
        // higher dispatcher priority than Loaded, so also hold the frame while a cross-window
        // Loaded batch is still preparing sibling targets.
        if (!_dispatcher.CheckAccess() ||
            _isTicking ||
            _pendingLoadedReconciles > 0)
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
            _postCommitCallbacks.Clear();
            _acceptingPostCommitCallbacks = true;
            bool nativeBatchCommitted;
            bool logicalBatchDeferred;
            bool logicalBatchFailed;
            bool frameCommitted;
            bool frameDeferred;
            using (_dispatcher.DisableProcessing())
            {
                using (var nativeBoundsBatch =
                    WindowNative.BeginWindowDeviceBoundsBatch(initialCount))
                {
                    // Iterate backwards without a per-frame snapshot allocation. Deactivation is
                    // deferred until the native batch commits; presenters activated during this
                    // tick start next time.
                    for (var index = initialCount - 1; index >= 0; index--)
                    {
                        var presenter = _presenters[index];
                        _ = presenter.AdvanceSharedFrame(
                            this,
                            pointer,
                            frameTimestamp);
                    }

                    _acceptingPostCommitCallbacks = false;
                    logicalBatchDeferred = false;
                    logicalBatchFailed = false;
                    for (var index = initialCount - 1; index >= 0; index--)
                    {
                        var presenter = _presenters[index];
                        if (!presenter.NativeBatchApplyActive)
                        {
                            continue;
                        }
                        switch (presenter.NativeBatchApplyStatus)
                        {
                            case EdgeCapsuleNativeBatchApplyStatus.Deferred:
                                logicalBatchDeferred = true;
                                break;
                            case EdgeCapsuleNativeBatchApplyStatus.Failed:
                                logicalBatchFailed = true;
                                break;
                        }
                    }
                    nativeBatchCommitted = nativeBoundsBatch.Commit();
                }

                frameDeferred = nativeBatchCommitted &&
                    logicalBatchDeferred &&
                    !logicalBatchFailed;
                frameCommitted = nativeBatchCommitted &&
                    !logicalBatchDeferred &&
                    !logicalBatchFailed;
                for (var index = initialCount - 1; index >= 0; index--)
                {
                    var presenter = _presenters[index];
                    if (frameCommitted)
                    {
                        presenter.CompleteNativeBatchApplySuccess();
                    }
                    else if (frameDeferred)
                    {
                        presenter.CompleteNativeBatchApplyDeferred();
                    }
                    else
                    {
                        presenter.CompleteNativeBatchApplyFailure(frameTimestamp);
                    }
                }
            }

            if (frameCommitted)
            {
                // Controller pointer resolution scans every presenter in the queue. Publish those
                // observations only after all in-memory frames and their native HWND bounds belong
                // to the same committed frame; otherwise the scan can mix sibling generations.
                for (var index = 0; index < _postCommitCallbacks.Count; index++)
                {
                    _postCommitCallbacks[index]();
                }
            }
            else
            {
                // A failed native batch has no publishable geometry. PaperWindow retains its
                // accumulated notification state. Every participating presenter was re-armed
                // above, so the shared scheduler retries the whole logical generation together.
                _postCommitCallbacks.Clear();
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
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
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
