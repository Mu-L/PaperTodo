namespace PaperTodo;

public sealed partial class AppController
{
    private bool AllowsEdgeCapsuleQueueProxyOwnership(string queueKey) =>
        !_windows.Values.Any(candidate =>
            !candidate.IsClosed &&
            !candidate.AllowsDeepCapsuleQueueProxyOwnership &&
            string.Equals(
                QueueKey(candidate.EdgeCapsulePreviewPaper),
                queueKey,
                StringComparison.Ordinal));

    private void CancelPreviewActivationBehindDrag() =>
        CancelEdgeCapsulePreviewActivationIntent();

    /// <summary>
    /// Physical pointer authority for edge-preview input. Host/native input may prove that the
    /// pointer is inside a real applied rectangle even while the Presenter's cosmetic hover bit is
    /// stale. The first card may therefore open from a verified physical hit; an existing session
    /// still uses the normal 50 ms / 2-DIP transfer contract.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewPhysicalPointer(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        if (IsExiting)
        {
            return;
        }

        // Once reorder starts, the drag gesture owns this queue's visible transition. Invalidate
        // work queued before that gesture change and never start a preview underneath it.
        if (!AllowsEdgeCapsuleQueueProxyOwnership(
                QueueKey(inputWindow.EdgeCapsulePreviewPaper)))
        {
            CancelPreviewActivationBehindDrag();
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null)
        {
            // During continuous browsing, the candidate spends the transfer-intent interval in its
            // compact hover shape. Prime that resize before the caller's Send reconcile so the real
            // HWND never exposes intermediate width frames.

            // Physical host input is only the wake-up authority. Once a preview session exists,
            // the owner remains the single queue-wide arbiter for owner/target/corridor/outside
            // resolution, transfer timing and close timing. Do not recreate that state machine in
            // this input adapter.
            if (_windows.TryGetValue(session.OwnerPaperId, out var owner))
            {
                NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
            }
            return;
        }

        ResetEdgeCapsulePreviewCorridorExitIntent();
        if (!pointer.HasValue)
        {
            return;
        }

        var point = pointer.Value;
        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(point);
        if (!inputWindow.CanEnterEdgeCapsulePreview ||
            !inputWindow.IsEdgeCapsuleInteractiveAt(point) ||
            IsEdgeCapsulePreviewLayoutSuppressedFor(inputWindow))
        {
            // With no preview transaction available, compact hover is the visible interaction and
            // therefore needs compositor ownership itself.
            CancelEdgeCapsulePreviewActivationIntent(
                inputWindow.EdgeCapsulePreviewPaperId);
            return;
        }

        // The first eligible card opens immediately from this verified physical hit. Do not insert
        // a redundant Resting→Hovered proxy immediately before the larger preview proxy; that would
        // add startup work and a visual phase the historical first-hit contract never had.
        if (!inputWindow.IsEdgeCapsulePointerOver)
        {
            TraceEdgeCapsulePreview(
                $"physical hit recovery target={EdgeCapsulePreviewTraceId(inputWindow.EdgeCapsulePreviewPaperId)} " +
                $"pointer={point.X},{point.Y}");
        }

        AdvanceEdgeCapsulePreviewActivationIntent(
            null,
            inputWindow,
            point);
    }
}
