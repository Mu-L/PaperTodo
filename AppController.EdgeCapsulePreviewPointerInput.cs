namespace PaperTodo;

public sealed partial class AppController
{
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

        // Consume the physical over/out bit before preview intent uses it. If that bit changes the
        // compact shell geometry, the queue compositor is installed synchronously while the real
        // HWND still matches the previous frame. The caller's normal Send reconcile then advances
        // Presenter state behind that cover instead of exposing a per-frame native resize.
        inputWindow.PrimeEdgeCapsulePointerComposition(pointer);

        var session = _edgeCapsulePreviewSession;
        if (session != null)
        {
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
            CancelEdgeCapsulePreviewActivationIntent(
                inputWindow.EdgeCapsulePreviewPaperId);
            return;
        }

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
