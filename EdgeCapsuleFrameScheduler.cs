using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One animation-frame scheduler per UI dispatcher. Presenters still own their transitions and
/// reconcile pipelines; the shared scheduler samples one pointer/time per frame, then commits each
/// monitor/edge queue independently so one bad HWND cannot hide unrelated queues.
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
        Schedulers.GetValue(
            dispatcher,
            static key => new EdgeCapsuleFrameScheduler(key));

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
            if (initialCount == 0)
            {
                return;
            }

            var frameTimestamp = Stopwatch.GetTimestamp();
            var pointer = WindowNative.TryGetCursorScreenPosition(
                out var currentPointer)
                    ? currentPointer
                    : (DeviceScreenPoint?)null;
            foreach (var group in BuildFrameGroups(initialCount))
            {
                AdvanceNativeBatchGroup(
                    group,
                    pointer,
                    frameTimestamp);
            }

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

    private IReadOnlyList<List<EdgeCapsulePresenter>> BuildFrameGroups(
        int initialCount)
    {
        var groups = new List<List<EdgeCapsulePresenter>>();
        var groupIndices =
            new Dictionary<EdgeCapsuleNativeBatchGroup, int>();
        for (var index = 0; index < initialCount; index++)
        {
            var presenter = _presenters[index];
            var key = presenter.NativeBatchGroup;
            if (!groupIndices.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groups.Count;
                groupIndices[key] = groupIndex;
                groups.Add(new List<EdgeCapsulePresenter>());
            }
            groups[groupIndex].Add(presenter);
        }
        return groups;
    }

    private void AdvanceNativeBatchGroup(
        IReadOnlyList<EdgeCapsulePresenter> presenters,
        DeviceScreenPoint? pointer,
        long frameTimestamp)
    {
        if (presenters.Count == 0)
        {
            return;
        }

        _postCommitCallbacks.Clear();
        _acceptingPostCommitCallbacks = true;
        var transactionGroupId =
            presenters[0].NativeBatchTransactionGroupId;
        try
        {
            bool nativeBatchCommitted;
            bool logicalBatchDeferred;
            bool logicalBatchFailed;
            bool frameCommitted;
            bool frameDeferred;
            using (_dispatcher.DisableProcessing())
            {
                using (var nativeBoundsBatch =
                    WindowNative.BeginWindowDeviceBoundsBatch(
                        presenters.Count))
                {
                    for (var index = presenters.Count - 1;
                         index >= 0;
                         index--)
                    {
                        _ = presenters[index].AdvanceSharedFrame(
                            this,
                            pointer,
                            frameTimestamp);
                    }

                    _acceptingPostCommitCallbacks = false;
                    logicalBatchDeferred = false;
                    logicalBatchFailed = false;
                    for (var index = presenters.Count - 1;
                         index >= 0;
                         index--)
                    {
                        var presenter = presenters[index];
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
                for (var index = presenters.Count - 1;
                     index >= 0;
                     index--)
                {
                    var presenter = presenters[index];
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
                        presenter.CompleteNativeBatchApplyFailure(
                            frameTimestamp);
                    }
                }
            }

            CompleteNativeBatchTransactionGroup(
                presenters,
                transactionGroupId,
                frameCommitted,
                frameDeferred);

            if (frameCommitted)
            {
                for (var index = 0;
                     index < _postCommitCallbacks.Count;
                     index++)
                {
                    _postCommitCallbacks[index]();
                }
            }
        }
        finally
        {
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
        }
    }

    private static void CompleteNativeBatchTransactionGroup(
        IReadOnlyList<EdgeCapsulePresenter> presenters,
        long transactionGroupId,
        bool frameCommitted,
        bool frameDeferred)
    {
        if (transactionGroupId <= 0)
        {
            return;
        }

        if (!frameCommitted && !frameDeferred &&
            presenters.Any(presenter =>
                presenter.NativeBatchTransactionRetryExhausted))
        {
            foreach (var presenter in presenters)
            {
                presenter.AbortNativeBatchTransactionGroup(
                    transactionGroupId);
            }
            return;
        }

        if (!frameCommitted ||
            presenters.Any(presenter =>
                !presenter.CanReleaseNativeBatchTransactionGroup(
                    transactionGroupId)))
        {
            return;
        }

        foreach (var presenter in presenters)
        {
            presenter.ReleaseNativeBatchTransactionGroup(
                transactionGroupId);
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
