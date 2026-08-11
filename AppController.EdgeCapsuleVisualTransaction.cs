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
    private long _edgeCapsuleNativeTransactionGroupGeneration;

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
            var transactionEntries = entries
                .Where(entry => transactionQueueKeys.Contains(entry.QueueKey))
                .ToArray();
            if (EdgeCapsuleNativeTransactionPolicy.RequiresCrossQueueGroup(
                    transactionEntries
                        .Where(entry => !entry.Window.IsClosed)
                        .Select(entry => entry.QueueKey)))
            {
                var transactionGroupId =
                    NextEdgeCapsuleNativeTransactionGroupId();
                foreach (var entry in transactionEntries)
                {
                    if (!entry.Window.IsClosed)
                    {
                        entry.Window.JoinEdgeCapsuleNativeTransactionGroup(
                            transactionGroupId);
                    }
                }
            }
            CommitEdgeCapsuleVisualTransactionGroup(
                transactionEntries,
                transactionQueueKeys,
                transactionTimestamp);

            foreach (var queueGroup in entries
                         .Where(entry =>
                             !transactionQueueKeys.Contains(entry.QueueKey))
                         .GroupBy(
                             entry => entry.QueueKey,
                             StringComparer.Ordinal))
            {
                CommitEdgeCapsuleVisualTransactionGroup(
                    queueGroup.ToArray(),
                    EmptyQueueKeySet,
                    transactionTimestamp);
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

    private static readonly IReadOnlySet<string> EmptyQueueKeySet =
        new HashSet<string>(StringComparer.Ordinal);

    private long NextEdgeCapsuleNativeTransactionGroupId()
    {
        unchecked
        {
            _edgeCapsuleNativeTransactionGroupGeneration++;
        }
        if (_edgeCapsuleNativeTransactionGroupGeneration <= 0)
        {
            _edgeCapsuleNativeTransactionGroupGeneration = 1;
        }
        return _edgeCapsuleNativeTransactionGroupGeneration;
    }

    private void CommitEdgeCapsuleVisualTransactionGroup(
        EdgeCapsuleVisualTransactionEntry[] entries,
        IReadOnlySet<string> transactionQueueKeys,
        long transactionTimestamp)
    {
        if (entries.Length == 0)
        {
            return;
        }

        var snapQueueKeys = entries
            .Where(entry =>
                !entry.Window.IsClosed &&
                transactionQueueKeys.Contains(entry.QueueKey) &&
                entry.Motion.Kind == EdgeCapsuleMotionKind.Snap)
            .Select(entry => entry.QueueKey)
            .ToHashSet(StringComparer.Ordinal);
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
                if (entry.Window.IsClosed)
                {
                    continue;
                }

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
                    // Global arrange can stage an unrelated queue. Preserve its in-flight target,
                    // but commit it in a separate native batch so another queue cannot poison it.
                    motion = EdgeCapsuleMotion.Preserve(motion.Reason);
                }

                var applyStatus = entry.Window.CommitEdgeCapsuleVisualTransaction(
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

        if (!transactionCommitted)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!entry.Window.IsClosed)
            {
                entry.Window.PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
    }
}
