using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private sealed record EdgeCapsuleVisualTransactionEntry(
        PaperWindow Window,
        string QueueKey,
        EdgeCapsuleMotion Motion,
        bool RefreshLayout);

    private readonly Dictionary<PaperWindow, EdgeCapsuleVisualTransactionEntry>
        _edgeCapsuleVisualTransactionEntries = new();
    private DispatcherOperation? _edgeCapsuleVisualTransactionCommitOperation;
    private readonly HashSet<string> _edgeCapsuleVisualTransactionQueueKeys =
        new(StringComparer.Ordinal);

    internal void BeginEdgeCapsuleVisualTransaction(PaperWindow initiator)
    {
        if (IsExiting)
        {
            return;
        }

        var queueKey = QueueKey(initiator.EdgeCapsulePreviewPaper);
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Pending })
        {
            // Cross-queue preview transfer calls Begin once for the new owner and once for the old
            // owner. Both logical queues belong to the same atomic visual transaction.
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            return;
        }
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Executing })
        {
            return;
        }

        _edgeCapsuleVisualTransactionQueueKeys.Clear();
        _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
        _edgeCapsuleVisualTransactionCommitOperation = initiator.Dispatcher.BeginInvoke(
            (Action)CommitEdgeCapsuleVisualTransaction,
            DispatcherPriority.Send);
    }

    internal bool TryStageEdgeCapsuleVisualTransaction(
        PaperWindow window,
        EdgeCapsuleMotion motion,
        bool refreshLayout)
    {
        if (IsExiting ||
            _edgeCapsuleVisualTransactionCommitOperation is not
                { Status: DispatcherOperationStatus.Pending })
        {
            return false;
        }

        if (_edgeCapsuleVisualTransactionEntries.TryGetValue(
                window,
                out var existing))
        {
            _edgeCapsuleVisualTransactionEntries[window] = existing with
            {
                Motion = MergeEdgeCapsuleVisualTransactionMotion(
                    existing.Motion,
                    motion),
                RefreshLayout = existing.RefreshLayout || refreshLayout
            };
        }
        else
        {
            _edgeCapsuleVisualTransactionEntries[window] =
                new EdgeCapsuleVisualTransactionEntry(
                    window,
                    QueueKey(window.EdgeCapsulePreviewPaper),
                    motion,
                    refreshLayout);
        }
        return true;
    }

    private static EdgeCapsuleMotion MergeEdgeCapsuleVisualTransactionMotion(
        EdgeCapsuleMotion existing,
        EdgeCapsuleMotion incoming)
    {
        // Match Presenter.RequestPresentation semantics: Snap owns the batch; otherwise the latest
        // explicit animation may replace an earlier animation/preserve request for the same window.
        if (incoming.Kind == EdgeCapsuleMotionKind.Snap)
        {
            return incoming;
        }
        if (existing.Kind == EdgeCapsuleMotionKind.Snap)
        {
            return existing;
        }
        return incoming.Kind == EdgeCapsuleMotionKind.Animate
            ? incoming
            : existing;
    }

    private void CommitEdgeCapsuleVisualTransaction()
    {
        var operation = _edgeCapsuleVisualTransactionCommitOperation;
        var transactionQueueKeys = _edgeCapsuleVisualTransactionQueueKeys
            .ToHashSet(StringComparer.Ordinal);
        var entries = _edgeCapsuleVisualTransactionEntries.Values.ToArray();
        _edgeCapsuleVisualTransactionEntries.Clear();
        try
        {
            if (entries.Length == 0 || IsExiting)
            {
                return;
            }

            TraceEdgeCapsulePreview(
                $"visual transaction commit count={entries.Length}");

            var transactionTimestamp = Stopwatch.GetTimestamp();
            var snapQueueKeys = entries
                .Where(entry =>
                    !entry.Window.IsClosed &&
                    transactionQueueKeys.Contains(entry.QueueKey) &&
                    entry.Motion.Kind == EdgeCapsuleMotionKind.Snap)
                .Select(entry => entry.QueueKey)
                .ToHashSet(StringComparer.Ordinal);

            // Desired preview state and every affected queue placement are already staged. Prevent
            // nested Dispatcher processing while each Presenter creates its transition from the
            // current applied frame. One timestamp gives every member of the initiating queue
            // identical progress; if one of those members must snap, snap that queue only so a
            // card cannot jump to the endpoint while its neighbours are still interpolating.
            var nativeBatchCommitted = true;
            var logicalBatchDeferred = false;
            var logicalBatchFailed = false;
            bool transactionCommitted;
            bool transactionDeferred;
            using (entries[0].Window.Dispatcher.DisableProcessing())
            {
                using var nativeBoundsBatch =
                    WindowNative.BeginWindowDeviceBoundsBatch(entries.Length);
                foreach (var entry in entries)
                {
                    if (!entry.Window.IsClosed)
                    {
                        var belongsToTransactionQueue =
                            transactionQueueKeys.Contains(entry.QueueKey);
                        var motion = entry.Motion;
                        if (belongsToTransactionQueue &&
                            snapQueueKeys.Contains(entry.QueueKey) &&
                            motion.Kind != EdgeCapsuleMotionKind.Snap)
                        {
                            motion = EdgeCapsuleMotion.Snap(motion.Reason);
                        }
                        else if (!belongsToTransactionQueue &&
                            motion.Kind == EdgeCapsuleMotionKind.Snap)
                        {
                            // The preview transaction owns one logical queue. A global arrange
                            // still stages sibling queues, but its Snap must not finish an unrelated
                            // in-flight animation whose target did not change.
                            motion = EdgeCapsuleMotion.Preserve(motion.Reason);
                        }

                        var applyStatus =
                            entry.Window.CommitEdgeCapsuleVisualTransaction(
                                motion,
                                entry.RefreshLayout,
                                transactionTimestamp,
                                rebaseActiveTransition: belongsToTransactionQueue);
                        if (applyStatus == EdgeCapsuleNativeBatchApplyStatus.Deferred)
                        {
                            logicalBatchDeferred = true;
                        }
                        else if (applyStatus == EdgeCapsuleNativeBatchApplyStatus.Failed)
                        {
                            logicalBatchFailed = true;
                        }
                    }
                }
                nativeBatchCommitted = nativeBoundsBatch.Commit();
                transactionDeferred = nativeBatchCommitted &&
                    logicalBatchDeferred &&
                    !logicalBatchFailed;
                transactionCommitted = nativeBatchCommitted &&
                    !logicalBatchDeferred &&
                    !logicalBatchFailed;
                foreach (var entry in entries)
                {
                    entry.Window.CompleteEdgeCapsuleVisualTransactionApply(
                        transactionCommitted,
                        transactionDeferred,
                        transactionTimestamp);
                }
            }

            if (transactionCommitted)
            {
                // Every Presenter now exposes the same logical generation and the HWND batch has
                // committed. Only now may queue-wide pointer resolution observe the transaction.
                foreach (var entry in entries)
                {
                    if (!entry.Window.IsClosed)
                    {
                        entry.Window.PublishEdgeCapsuleVisualTransactionNotifications();
                    }
                }
            }
        }
        finally
        {
            if (ReferenceEquals(
                    operation,
                    _edgeCapsuleVisualTransactionCommitOperation))
            {
                _edgeCapsuleVisualTransactionCommitOperation = null;
                _edgeCapsuleVisualTransactionQueueKeys.Clear();
            }
        }
    }
}
