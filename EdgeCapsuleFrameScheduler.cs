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
    private TimeSpan? _lastRenderingTime;
#if DEBUG
    private long _lastRenderingTimestamp;
    private long _debugFrameSequence;
    private int _suppressedDuplicateRenderingCallbacks;
#endif

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

        var renderingTime = e is RenderingEventArgs renderingArgs
            ? renderingArgs.RenderingTime
            : (TimeSpan?)null;
        if (renderingTime.HasValue &&
            _lastRenderingTime.HasValue &&
            renderingTime.Value == _lastRenderingTime.Value)
        {
#if DEBUG
            _suppressedDuplicateRenderingCallbacks++;
#endif
            return;
        }
        _lastRenderingTime = renderingTime;

#if DEBUG
        var callbackStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var frameSequence = ++_debugFrameSequence;
        var frameGapMilliseconds = _lastRenderingTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _lastRenderingTimestamp,
                callbackStartedAt);
        _lastRenderingTimestamp = callbackStartedAt;
        var debugInitialCount = 0;
        var debugGroupCount = 0;
        var duplicateRenderingCallbacks = _suppressedDuplicateRenderingCallbacks;
        _suppressedDuplicateRenderingCallbacks = 0;
        var renderingTimeMilliseconds = renderingTime?.TotalMilliseconds ?? -1;
#endif
        _isTicking = true;
        try
        {
            var initialCount = _presenters.Count;
#if DEBUG
            debugInitialCount = initialCount;
#endif
            if (initialCount == 0)
            {
                return;
            }

            var frameTimestamp = Stopwatch.GetTimestamp();
            var pointer = WindowNative.TryGetCursorScreenPosition(
                out var currentPointer)
                    ? currentPointer
                    : (DeviceScreenPoint?)null;
            var groups = BuildFrameGroups(initialCount);
#if DEBUG
            debugGroupCount = groups.Count;
#endif
            foreach (var group in groups)
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
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.frame sequence={frameSequence} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callbackStartedAt):F3} " +
                $"gapMs={frameGapMilliseconds:F3} renderMs={renderingTimeMilliseconds:F3} " +
                $"duplicateCallbacks={duplicateRenderingCallbacks} presenters={debugInitialCount} " +
                $"groups={debugGroupCount} loadedPending={_pendingLoadedReconciles}");
#endif
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
#if DEBUG
        var groupStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        double reconcileMilliseconds = 0;
        double statusMilliseconds = 0;
        double nativeCommitMilliseconds = 0;
        double completionMilliseconds = 0;
        double postCommitMilliseconds = 0;
        double slowestPresenterMilliseconds = 0;
        var slowestPresenter = "<none>";
        var debugOutcome = "exception";
        var boundsRequested = 0;
        var boundsPending = 0;
        var boundsUnchanged = 0;
        var boundsMoveChanges = 0;
        var boundsSizeChanges = 0;
#endif
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
#if DEBUG
                        var presenterStartedAt =
                            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                        _ = presenters[index].AdvanceSharedFrame(
                            this,
                            pointer,
                            frameTimestamp);
#if DEBUG
                        var presenterMilliseconds =
                            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                                presenterStartedAt);
                        reconcileMilliseconds += presenterMilliseconds;
                        if (presenterMilliseconds > slowestPresenterMilliseconds)
                        {
                            slowestPresenterMilliseconds = presenterMilliseconds;
                            slowestPresenter = presenters[index].DiagnosticId;
                        }
#endif
                    }

                    _acceptingPostCommitCallbacks = false;
                    logicalBatchDeferred = false;
                    logicalBatchFailed = false;
#if DEBUG
                    var statusStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
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
#if DEBUG
                    statusMilliseconds +=
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            statusStartedAt);
                    var nativeCommitStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                    nativeBatchCommitted = nativeBoundsBatch.Commit();
#if DEBUG
                    nativeCommitMilliseconds +=
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            nativeCommitStartedAt);
                    boundsRequested = nativeBoundsBatch.RequestedWindowCount;
                    boundsPending = nativeBoundsBatch.PendingWindowCount;
                    boundsUnchanged = nativeBoundsBatch.UnchangedWindowCount;
                    boundsMoveChanges = nativeBoundsBatch.MoveChangeCount;
                    boundsSizeChanges = nativeBoundsBatch.SizeChangeCount;
#endif
                }

                frameDeferred = nativeBatchCommitted &&
                    logicalBatchDeferred &&
                    !logicalBatchFailed;
                frameCommitted = nativeBatchCommitted &&
                    !logicalBatchDeferred &&
                    !logicalBatchFailed;
#if DEBUG
                var completionStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
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
#if DEBUG
                completionMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        completionStartedAt);
#endif
            }

#if DEBUG
            var groupCompletionStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            CompleteNativeBatchTransactionGroup(
                presenters,
                transactionGroupId,
                frameCommitted,
                frameDeferred);

            if (frameCommitted)
            {
#if DEBUG
                completionMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        groupCompletionStartedAt);
                var postCommitStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                for (var index = 0;
                     index < _postCommitCallbacks.Count;
                     index++)
                {
                    _postCommitCallbacks[index]();
                }
#if DEBUG
                postCommitMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        postCommitStartedAt);
#endif
            }
#if DEBUG
            if (!frameCommitted)
            {
                completionMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        groupCompletionStartedAt);
            }
            debugOutcome = frameCommitted
                ? "committed"
                : frameDeferred
                    ? "deferred"
                    : "failed";
#endif
        }
        finally
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.group sequence={_debugFrameSequence} outcome={debugOutcome} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(groupStartedAt):F3} " +
                $"reconcileMs={reconcileMilliseconds:F3} statusMs={statusMilliseconds:F3} " +
                $"nativeCommitMs={nativeCommitMilliseconds:F3} completeMs={completionMilliseconds:F3} " +
                $"postCommitMs={postCommitMilliseconds:F3} presenters={presenters.Count} " +
                $"boundsRequested={boundsRequested} boundsPending={boundsPending} " +
                $"boundsUnchanged={boundsUnchanged} moveChanges={boundsMoveChanges} " +
                $"sizeChanges={boundsSizeChanges} slowest={slowestPresenter}:{slowestPresenterMilliseconds:F3} " +
                $"transaction={transactionGroupId}");
#endif
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
            _lastRenderingTime = null;
#if DEBUG
            _lastRenderingTimestamp = 0;
            _suppressedDuplicateRenderingCallbacks = 0;
#endif
        }
    }
}
