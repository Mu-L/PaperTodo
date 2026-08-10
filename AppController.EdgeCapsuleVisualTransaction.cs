using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private sealed record EdgeCapsuleVisualTransactionEntry(
        PaperWindow Window,
        EdgeCapsuleMotion Motion,
        bool RefreshLayout);

    private readonly Dictionary<PaperWindow, EdgeCapsuleVisualTransactionEntry>
        _edgeCapsuleVisualTransactionEntries = new();
    private DispatcherOperation? _edgeCapsuleVisualTransactionCommitOperation;

    internal void BeginEdgeCapsuleVisualTransaction(PaperWindow initiator)
    {
        if (IsExiting ||
            _edgeCapsuleVisualTransactionCommitOperation is
                { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            return;
        }

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
            var snapBatch = entries.Any(entry =>
                !entry.Window.IsClosed &&
                entry.Motion.Kind == EdgeCapsuleMotionKind.Snap);

            // Desired preview state and every affected queue placement are already staged. Prevent
            // nested Dispatcher processing while each Presenter creates its transition from the
            // current applied frame. One timestamp gives every queue member identical progress;
            // if any staged correction must snap, snap the whole batch so one card cannot jump to
            // the endpoint while its neighbours are still interpolating toward it.
            var nativeBatchCommitted = true;
            using (entries[0].Window.Dispatcher.DisableProcessing())
            {
                using var nativeBoundsBatch =
                    WindowNative.BeginWindowDeviceBoundsBatch(entries.Length);
                foreach (var entry in entries)
                {
                    if (!entry.Window.IsClosed)
                    {
                        entry.Window.CommitEdgeCapsuleVisualTransaction(
                            snapBatch &&
                                entry.Motion.Kind != EdgeCapsuleMotionKind.Snap
                                ? EdgeCapsuleMotion.Snap(entry.Motion.Reason)
                                : entry.Motion,
                            entry.RefreshLayout,
                            transactionTimestamp);
                    }
                }
                nativeBatchCommitted = nativeBoundsBatch.Commit();
            }

            if (!nativeBatchCommitted)
            {
                foreach (var entry in entries)
                {
                    if (!entry.Window.IsClosed)
                    {
                        entry.Window.RetryEdgeCapsuleVisualTransaction();
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
            }
        }
    }
}
