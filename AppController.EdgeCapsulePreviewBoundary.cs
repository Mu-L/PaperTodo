using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _edgeCapsulePreviewBoundaryLeaveTimer;
    private string? _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
    private int _edgeCapsulePreviewBoundaryLeaveTransferGeneration;

    /// <summary>
    /// A verified native hit/move inside a committed capsule is fresh physical activity and may
    /// revoke a pending WM_MOUSELEAVE confirmation. Routed WPF enter/leave never calls this path.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewHostPointerActivity()
    {
        var ownerPaperId = _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (ownerPaperId == null)
        {
            return;
        }

        TraceEdgeCapsulePreview(
            $"boundary confirm cancelled owner={EdgeCapsulePreviewTraceId(ownerPaperId)} " +
            "reason=verified-host-activity");
        ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        ForgetEdgeCapsulePreviewPointerResolution();
    }

    /// <summary>
    /// WM_MOUSELEAVE may arrive while the cursor still resolves inside the last committed
    /// InteractiveBounds. The following pixels can already be HTTRANSPARENT, so no later HWND move
    /// is guaranteed. Keep a narrow physical-cursor watcher alive until that ambiguity resolves,
    /// then return to the existing owner/target/corridor arbiter rather than creating another FSM.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewHostBoundaryLeave(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        NotifyEdgeCapsulePreviewPhysicalPointer(inputWindow, pointer);

        if (IsExiting ||
            !pointer.HasValue ||
            _edgeCapsulePreviewSession is not { } session ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive ||
            !AllowsEdgeCapsuleQueueProxyOwnership(session.QueueKey))
        {
            return;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (!resolution.OwnerContains)
        {
            // The canonical arbiter already started transfer/corridor/hard-close work.
            return;
        }

        _edgeCapsulePreviewBoundaryLeaveOwnerPaperId =
            session.OwnerPaperId;
        _edgeCapsulePreviewBoundaryLeaveTransferGeneration =
            _edgeCapsulePreviewTransferGeneration;
        ForgetEdgeCapsulePreviewPointerResolution();
        TraceEdgeCapsulePreview(
            $"boundary confirm armed owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.Value.X},{pointer.Value.Y}");
        ScheduleEdgeCapsulePreviewHostBoundaryLeaveConfirmation(owner);
    }

    private void ScheduleEdgeCapsulePreviewHostBoundaryLeaveConfirmation(
        PaperWindow owner)
    {
        if (_edgeCapsulePreviewBoundaryLeaveOwnerPaperId == null)
        {
            return;
        }

        if (_edgeCapsulePreviewBoundaryLeaveTimer == null)
        {
            _edgeCapsulePreviewBoundaryLeaveTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                owner.Dispatcher);
            _edgeCapsulePreviewBoundaryLeaveTimer.Tick +=
                OnEdgeCapsulePreviewBoundaryLeaveTimerTick;
        }

        _edgeCapsulePreviewBoundaryLeaveTimer.Stop();
        _edgeCapsulePreviewBoundaryLeaveTimer.Interval =
            TimeSpan.FromMilliseconds(
                EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds);
        _edgeCapsulePreviewBoundaryLeaveTimer.Start();
    }

    private void OnEdgeCapsulePreviewBoundaryLeaveTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewBoundaryLeaveTimer?.Stop();
        var expectedOwnerPaperId =
            _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (expectedOwnerPaperId == null ||
            IsExiting ||
            _edgeCapsulePreviewBoundaryLeaveTransferGeneration !=
                _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                session.OwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !AllowsEdgeCapsuleQueueProxyOwnership(session.QueueKey))
        {
            ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return;
        }

        if (!owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            ForgetEdgeCapsulePreviewPointerResolution();
            NotifyEdgeCapsulePreviewPointerSample(owner, null);
            return;
        }
        if (owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return;
        }
        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // A transient read failure is not positive exit evidence. Keep this tiny watcher alive;
            // the ordinary corridor/no-target timers begin only after a readable point leaves owner.
            ScheduleEdgeCapsulePreviewHostBoundaryLeaveConfirmation(owner);
            return;
        }

        ForgetEdgeCapsulePreviewPointerResolution();
        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer);
        if (resolution.OwnerContains)
        {
            ScheduleEdgeCapsulePreviewHostBoundaryLeaveConfirmation(owner);
            return;
        }

        TraceEdgeCapsulePreview(
            $"boundary confirm resolved owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.X},{pointer.Y} " +
            $"target={EdgeCapsulePreviewTraceId(resolution.Target?.EdgeCapsulePreviewPaperId)} " +
            $"transferContains={resolution.TransferRectangleContains}");
        ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        ForgetEdgeCapsulePreviewPointerResolution();
        NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
    }

    private void ResetEdgeCapsulePreviewHostBoundaryLeaveConfirmation()
    {
        _edgeCapsulePreviewBoundaryLeaveOwnerPaperId = null;
        _edgeCapsulePreviewBoundaryLeaveTimer?.Stop();
    }
}
