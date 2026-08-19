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
    private const int TransitionLivenessWatchdogMilliseconds = 12;
    private static readonly ConditionalWeakTable<Dispatcher, EdgeCapsuleFrameScheduler> Schedulers = new();

    private readonly Dispatcher _dispatcher;
    private readonly List<EdgeCapsulePresenter> _presenters = new();
    private readonly List<Action> _postCommitCallbacks = new();
    private readonly List<List<EdgeCapsulePresenter>> _frameGroups = new();
    private readonly Dictionary<EdgeCapsuleNativeBatchGroup, int> _frameGroupIndices = new();
    private readonly DispatcherTimer _transitionLivenessWatchdog;
    private bool _renderingSubscribed;
    private bool _isTicking;
    private bool _acceptingPostCommitCallbacks;
    private int _pendingRenderReconciles;
    private TimeSpan? _lastRenderingTime;
#if DEBUG
    private long _lastRenderingTimestamp;
    private long _debugFrameSequence;
    private int _suppressedDuplicateRenderingCallbacks;
    private int _suppressedPendingReconcileRenderingCallbacks;
    private int _suppressedExternalNativeBatchRenderingCallbacks;
    private int _suppressedReentrantRenderingCallbacks;
    private long _suppressedRenderingStartedAtTimestamp;
#endif

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _transitionLivenessWatchdog = new DispatcherTimer(
            DispatcherPriority.Render,
            dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(
                TransitionLivenessWatchdogMilliseconds)
        };
        _transitionLivenessWatchdog.Tick += OnTransitionLivenessWatchdog;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(
            dispatcher,
            static key => new EdgeCapsuleFrameScheduler(key));

    public void RegisterRenderReconcile()
    {
        _dispatcher.VerifyAccess();
        _pendingRenderReconciles++;
    }

    public void CompleteRenderReconcile()
    {
        _dispatcher.VerifyAccess();
        if (_pendingRenderReconciles > 0)
        {
            _pendingRenderReconciles--;
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

    private bool HasExternallyOwnedNativeBatchApply()
    {
        // The scheduler always completes its own BeginNativeBatchApply calls before _isTicking is
        // cleared. Therefore an active apply observed here belongs to a controller-owned visual
        // transaction that was synchronously re-entered by native HWND message dispatch.
        for (var index = 0; index < _presenters.Count; index++)
        {
            if (EdgeCapsuleNativeTransactionPolicy.ShouldDeferSharedFrameForNativeApply(
                    _presenters[index].NativeBatchApplyActive))
            {
                return true;
            }
        }
        return false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            return;
        }
        if (_isTicking)
        {
#if DEBUG
            RecordSuppressedRenderingCallback(ref _suppressedReentrantRenderingCallbacks);
#endif
            return;
        }
        if (_pendingRenderReconciles > 0)
        {
#if DEBUG
            RecordSuppressedRenderingCallback(ref _suppressedPendingReconcileRenderingCallbacks);
#endif
            return;
        }
        if (HasExternallyOwnedNativeBatchApply())
        {
#if DEBUG
            RecordSuppressedRenderingCallback(ref _suppressedExternalNativeBatchRenderingCallbacks);
#endif
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

        // A genuine composition callback arrived before the one-shot rescue. Cancel it first;
        // this keeps the watchdog demand-driven rather than turning it into a second frame clock.
        _transitionLivenessWatchdog.Stop();
        AdvanceSharedFrame(renderingTime, source: "render");
    }

    private void OnTransitionLivenessWatchdog(object? sender, EventArgs e)
    {
        _transitionLivenessWatchdog.Stop();
        if (!_dispatcher.CheckAccess() ||
            _dispatcher.HasShutdownStarted ||
            _presenters.Count == 0)
        {
            return;
        }

        var blockedByPendingReconcile = _pendingRenderReconciles > 0;
        var blockedByExternalNativeBatch =
            !blockedByPendingReconcile && HasExternallyOwnedNativeBatchApply();
        if (_isTicking ||
            blockedByPendingReconcile ||
            blockedByExternalNativeBatch)
        {
#if DEBUG
            var reason = _isTicking
                ? "reentrant"
                : blockedByPendingReconcile
                    ? "pending-reconcile"
                    : "external-native-batch";
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.watchdog phase=deferred reason={reason} " +
                $"presenters={_presenters.Count} renderPending={_pendingRenderReconciles}");
#endif
            ArmTransitionLivenessWatchdog();
            return;
        }

        AdvanceSharedFrame(renderingTime: null, source: "watchdog");
    }

    private void AdvanceSharedFrame(TimeSpan? renderingTime, string source)
    {
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
        var suppressedPendingCallbacks = _suppressedPendingReconcileRenderingCallbacks;
        var suppressedExternalCallbacks = _suppressedExternalNativeBatchRenderingCallbacks;
        var suppressedReentrantCallbacks = _suppressedReentrantRenderingCallbacks;
        var suppressedSpanMilliseconds = _suppressedRenderingStartedAtTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _suppressedRenderingStartedAtTimestamp,
                callbackStartedAt);
        _suppressedPendingReconcileRenderingCallbacks = 0;
        _suppressedExternalNativeBatchRenderingCallbacks = 0;
        _suppressedReentrantRenderingCallbacks = 0;
        _suppressedRenderingStartedAtTimestamp = 0;
        var renderingTimeMilliseconds = renderingTime?.TotalMilliseconds ?? -1;
#endif
        var anyCommittedApply = false;
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
            var groupCount = BuildFrameGroups(initialCount);
#if DEBUG
            debugGroupCount = groupCount;
#endif
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                anyCommittedApply |= AdvanceNativeBatchGroup(
                    _frameGroups[groupIndex],
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
                $"scheduler.frame sequence={frameSequence} source={source} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callbackStartedAt):F3} " +
                $"gapMs={frameGapMilliseconds:F3} renderMs={renderingTimeMilliseconds:F3} " +
                $"committedApply={anyCommittedApply} activeTransition={HasActiveTransitionPresenter()} " +
                $"duplicateCallbacks={duplicateRenderingCallbacks} presenters={debugInitialCount} " +
                $"groups={debugGroupCount} renderPending={_pendingRenderReconciles} " +
                $"skippedPending={suppressedPendingCallbacks} " +
                $"skippedExternal={suppressedExternalCallbacks} " +
                $"skippedReentrant={suppressedReentrantCallbacks} " +
                $"skipSpanMs={suppressedSpanMilliseconds:F3}");
#endif
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
            ClearFrameGroups();
            _isTicking = false;
            StopWhenEmpty();
            if (_presenters.Count > 0 &&
                HasActiveTransitionPresenter())
            {
                ArmTransitionLivenessWatchdog();
            }
        }
    }

    private bool HasActiveTransitionPresenter()
    {
        for (var index = 0; index < _presenters.Count; index++)
        {
            if (_presenters[index].HasActiveTransition)
            {
                return true;
            }
        }
        return false;
    }

    private void ArmTransitionLivenessWatchdog()
    {
        if (_presenters.Count == 0 || _dispatcher.HasShutdownStarted)
        {
            _transitionLivenessWatchdog.Stop();
            return;
        }

        _transitionLivenessWatchdog.Stop();
        _transitionLivenessWatchdog.Start();
    }

    private int BuildFrameGroups(int initialCount)
    {
        _frameGroupIndices.Clear();
        var groupCount = 0;
        for (var index = 0; index < initialCount; index++)
        {
            var presenter = _presenters[index];
            var key = presenter.NativeBatchGroup;
            if (!_frameGroupIndices.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groupCount++;
                _frameGroupIndices[key] = groupIndex;
                if (groupIndex == _frameGroups.Count)
                {
                    _frameGroups.Add(new List<EdgeCapsulePresenter>());
                }
            }
            _frameGroups[groupIndex].Add(presenter);
        }
        return groupCount;
    }

    private void ClearFrameGroups()
    {
        _frameGroupIndices.Clear();
        for (var index = 0; index < _frameGroups.Count; index++)
        {
            _frameGroups[index].Clear();
        }
    }

#if DEBUG
    private void RecordSuppressedRenderingCallback(ref int counter)
    {
        if (_suppressedRenderingStartedAtTimestamp == 0)
        {
            _suppressedRenderingStartedAtTimestamp =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
        }
        if (counter < int.MaxValue)
        {
            counter++;
        }
    }
#endif

    private bool AdvanceNativeBatchGroup(
        IReadOnlyList<EdgeCapsulePresenter> presenters,
        DeviceScreenPoint? pointer,
        long frameTimestamp)
    {
        if (presenters.Count == 0)
        {
            return false;
        }

        long nativeCommitVersionBefore = 0;
        for (var index = 0; index < presenters.Count; index++)
        {
            nativeCommitVersionBefore += presenters[index].NativeBatchCommitVersion;
        }

        _postCommitCallbacks.Clear();
        _acceptingPostCommitCallbacks = true;
        var transactionGroupId =
            presenters[0].NativeBatchTransactionGroupId;
        // A positive transaction id means several physical queues are still completing one
        // controller-owned visual transaction and need the existing atomic HDWP commit. Ordinary
        // proxy-backed preview animation has no HWND frame work here: real hosts already sit at the
        // endpoint and DirectComposition advances its visual tree. Fallback paths may still issue
        // direct X/Y changes;
        // sending those through EndDeferWindowPos repeatedly blocks the UI thread for 10-20+ ms on
        // affected systems. Dispatcher processing stays disabled for the whole group, so no input
        // or app callback can observe an interleaved logical frame.
        var useNativeBoundsBatch = transactionGroupId > 0;
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
        var nativeMode = useNativeBoundsBatch ? "batch" : "direct";
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
                WindowNative.WindowDeviceBoundsBatch? nativeBoundsBatch = null;
                try
                {
                    if (useNativeBoundsBatch)
                    {
                        nativeBoundsBatch = WindowNative.BeginWindowDeviceBoundsBatch(
                            presenters.Count);
                    }

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
                    nativeBatchCommitted = nativeBoundsBatch?.Commit() ?? true;
#if DEBUG
                    nativeCommitMilliseconds +=
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            nativeCommitStartedAt);
                    if (nativeBoundsBatch != null)
                    {
                        boundsRequested = nativeBoundsBatch.RequestedWindowCount;
                        boundsPending = nativeBoundsBatch.PendingWindowCount;
                        boundsUnchanged = nativeBoundsBatch.UnchangedWindowCount;
                        boundsMoveChanges = nativeBoundsBatch.MoveChangeCount;
                        boundsSizeChanges = nativeBoundsBatch.SizeChangeCount;
                    }
#endif
                }
                finally
                {
                    nativeBoundsBatch?.Dispose();
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
                $"sizeChanges={boundsSizeChanges} nativeMode={nativeMode} " +
                $"slowest={slowestPresenter}:{slowestPresenterMilliseconds:F3} " +
                $"transaction={transactionGroupId}");
#endif
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
        }

        long nativeCommitVersionAfter = 0;
        for (var index = 0; index < presenters.Count; index++)
        {
            nativeCommitVersionAfter += presenters[index].NativeBatchCommitVersion;
        }
        return nativeCommitVersionAfter != nativeCommitVersionBefore;
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
            _transitionLivenessWatchdog.Stop();
            CompositionTarget.Rendering -= OnRendering;
            _renderingSubscribed = false;
            _lastRenderingTime = null;
#if DEBUG
            _lastRenderingTimestamp = 0;
            _suppressedDuplicateRenderingCallbacks = 0;
            _suppressedPendingReconcileRenderingCallbacks = 0;
            _suppressedExternalNativeBatchRenderingCallbacks = 0;
            _suppressedReentrantRenderingCallbacks = 0;
            _suppressedRenderingStartedAtTimestamp = 0;
#endif
            ClearFrameGroups();
        }
    }
}
