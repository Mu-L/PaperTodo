using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly record struct EdgeCapsulePreviewPhysicalLinger(
        string OwnerPaperId,
        bool Predictive,
        long EnteredTimestamp,
        DeviceScreenPoint StableAnchor,
        long StableSinceTimestamp,
        long? DirectionAwaySinceTimestamp);

    private EdgeCapsulePreviewPhysicalLinger?
        _edgeCapsulePreviewPhysicalLinger;
    private DispatcherTimer? _edgeCapsulePreviewPhysicalLingerTimer;

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
            ResetEdgeCapsulePreviewPhysicalLinger();
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            ResetEdgeCapsulePreviewPhysicalLinger();
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
            return;
        }

        if (!pointer.HasValue ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewPhysicalLinger();
            return;
        }

        var current = pointer.Value;
        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(current);
        if (owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            ResetEdgeCapsulePreviewPhysicalLinger();
            return;
        }

        ObserveEdgeCapsulePreviewPointer(owner, current);
        var resolution = ResolveEdgeCapsulePreviewPointer(session, current);
        if (resolution.OwnerContains)
        {
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            ResetEdgeCapsulePreviewPhysicalLinger();
            RememberEdgeCapsulePreviewPointerResolution(session, current);
            return;
        }

        if (resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            ResetEdgeCapsulePreviewPhysicalLinger();
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(resolution.Target))
            {
                CancelEdgeCapsulePreviewActivationIntent();
                RememberEdgeCapsulePreviewPointerResolution(session, current);
                return;
            }

            AdvanceEdgeCapsulePreviewActivationIntent(
                session,
                resolution.Target,
                current);
            return;
        }

        CancelEdgeCapsulePreviewActivationIntent();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        var predictive = State.ExperimentalEdgeCapsuleHoverIntent;
        if (!predictive && !resolution.CorridorContains)
        {
            ResetEdgeCapsulePreviewPhysicalLinger();
            RememberEdgeCapsulePreviewPointerResolution(session, current);
            TraceEdgeCapsulePreview(
                $"physical corridor exit owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"predictive=False pointer={current.X},{current.Y}");
            QueueEdgeCapsulePreviewClose(
                owner,
                session.OwnerPaperId,
                EdgeCapsulePreviewCloseReason.OutsideCorridor);
            return;
        }

        CancelQueuedEdgeCapsulePreviewClose();
        AdvanceEdgeCapsulePreviewPhysicalLinger(
            owner,
            session,
            current,
            predictive,
            resolution.CorridorContains);
        RememberEdgeCapsulePreviewPointerResolution(session, current);
    }

    private void AdvanceEdgeCapsulePreviewPhysicalLinger(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer,
        bool predictive,
        bool corridorContains)
    {
        var now = Stopwatch.GetTimestamp();
        var linger = _edgeCapsulePreviewPhysicalLinger;
        var firstSample = !linger.HasValue ||
            !string.Equals(
                linger.Value.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            linger.Value.Predictive != predictive;
        var current = firstSample
            ? new EdgeCapsulePreviewPhysicalLinger(
                session.OwnerPaperId,
                predictive,
                now,
                pointer,
                now,
                null)
            : linger!.Value;

        double dpiScaleX;
        double dpiScaleY;
        if (owner.TryGetEdgeCapsuleAppliedGeometry(out var ownerGeometry))
        {
            dpiScaleX = ownerGeometry.DpiScaleX;
            dpiScaleY = ownerGeometry.DpiScaleY;
        }
        else if (WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                pointer,
                out var monitor))
        {
            dpiScaleX = monitor.DpiScaleX;
            dpiScaleY = monitor.DpiScaleY;
        }
        else
        {
            ResetEdgeCapsulePreviewPhysicalLinger();
            return;
        }

        if (predictive && EdgeCapsulePreviewPointerMovedBeyondTolerance(
                current.StableAnchor,
                pointer,
                dpiScaleX,
                dpiScaleY))
        {
            current = current with
            {
                StableAnchor = pointer,
                StableSinceTimestamp = now
            };
        }

        var enteredElapsed = Stopwatch.GetElapsedTime(
            current.EnteredTimestamp,
            now).TotalMilliseconds;
        var stableElapsed = Stopwatch.GetElapsedTime(
            current.StableSinceTimestamp,
            now).TotalMilliseconds;
        var directionAwayElapsed = current.DirectionAwaySinceTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(
                current.DirectionAwaySinceTimestamp.Value,
                now).TotalMilliseconds
            : 0;

        EdgeCapsuleCorridorExitDecision decision;
        if (!predictive)
        {
            decision = enteredElapsed >=
                EdgeCapsulePreviewFixedCorridorCloseMilliseconds
                ? EdgeCapsuleCorridorExitDecision.CloseForIdle
                : EdgeCapsuleCorridorExitDecision.KeepAlive;
        }
        else
        {
            Span<DeviceScreenRect> keepAliveBounds =
                session.QueuePaperIds.Count <= 32
                    ? stackalloc DeviceScreenRect[session.QueuePaperIds.Count]
                    : new DeviceScreenRect[session.QueuePaperIds.Count];
            var keepAliveCount = 0;
            foreach (var paperId in session.QueuePaperIds)
            {
                if (!_windows.TryGetValue(paperId, out var candidate) ||
                    !candidate.CanEnterEdgeCapsulePreview ||
                    !candidate.TryGetEdgeCapsuleInteractiveGeometry(
                        out var geometry))
                {
                    continue;
                }

                keepAliveBounds[keepAliveCount++] = geometry.Bounds;
            }

            decision = _edgeCapsulePreviewIntentPredictor.EvaluateCorridorExit(
                State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                keepAliveBounds.Slice(0, keepAliveCount),
                pointer,
                directionAwayElapsed,
                stableElapsed);
        }

        if (firstSample)
        {
            TraceEdgeCapsulePreview(
                $"physical linger enter owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"predictive={predictive} corridor={corridorContains} " +
                $"pointer={pointer.X},{pointer.Y}");
        }

        switch (decision)
        {
            case EdgeCapsuleCorridorExitDecision.ConfirmDirectionExit:
                current = current with
                {
                    DirectionAwaySinceTimestamp =
                        current.DirectionAwaySinceTimestamp ?? now
                };
                break;

            case EdgeCapsuleCorridorExitDecision.CloseForDirection:
            case EdgeCapsuleCorridorExitDecision.CloseForIdle:
                TraceEdgeCapsulePreview(
                    $"physical linger close owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                    $"reason={decision} predictive={predictive} corridor={corridorContains} " +
                    $"enteredMs={enteredElapsed:F1} awayMs={directionAwayElapsed:F1} " +
                    $"stableMs={stableElapsed:F1} pointer={pointer.X},{pointer.Y}");
                QueueEdgeCapsulePreviewClose(
                    owner,
                    session.OwnerPaperId,
                    EdgeCapsulePreviewCloseReason.CorridorIntent);
                ResetEdgeCapsulePreviewPhysicalLinger();
                return;

            default:
                current = current with
                {
                    DirectionAwaySinceTimestamp = null
                };
                break;
        }

        _edgeCapsulePreviewPhysicalLinger = current;
        ScheduleEdgeCapsulePreviewPhysicalLinger(owner, now);
    }

    private void ScheduleEdgeCapsulePreviewPhysicalLinger(
        PaperWindow owner,
        long now)
    {
        if (_edgeCapsulePreviewPhysicalLinger is not { } linger)
        {
            return;
        }

        var closeMilliseconds = linger.Predictive
            ? _edgeCapsulePreviewIntentPredictor.CorridorIdleCloseMilliseconds(
                State.ExperimentalEdgeCapsuleHoverIntentSensitivity)
            : EdgeCapsulePreviewFixedCorridorCloseMilliseconds;
        var elapsed = Stopwatch.GetElapsedTime(
            linger.Predictive
                ? linger.StableSinceTimestamp
                : linger.EnteredTimestamp,
            now).TotalMilliseconds;
        var remaining = Math.Max(1, closeMilliseconds - elapsed);
        var next = Math.Min(
            EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds,
            remaining);

        if (_edgeCapsulePreviewPhysicalLingerTimer == null)
        {
            _edgeCapsulePreviewPhysicalLingerTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                owner.Dispatcher);
            _edgeCapsulePreviewPhysicalLingerTimer.Tick +=
                OnEdgeCapsulePreviewPhysicalLingerTimerTick;
        }

        _edgeCapsulePreviewPhysicalLingerTimer.Stop();
        _edgeCapsulePreviewPhysicalLingerTimer.Interval =
            TimeSpan.FromMilliseconds(next);
        _edgeCapsulePreviewPhysicalLingerTimer.Start();
    }

    private void OnEdgeCapsulePreviewPhysicalLingerTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewPhysicalLingerTimer?.Stop();
        if (IsExiting ||
            _edgeCapsulePreviewPhysicalLinger is not { } linger ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                linger.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            ResetEdgeCapsulePreviewPhysicalLinger();
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            ScheduleEdgeCapsulePreviewPhysicalLinger(
                owner,
                Stopwatch.GetTimestamp());
            return;
        }

        NotifyEdgeCapsulePreviewPhysicalPointer(owner, pointer);
    }

    private void ResetEdgeCapsulePreviewPhysicalLinger()
    {
        _edgeCapsulePreviewPhysicalLinger = null;
        _edgeCapsulePreviewPhysicalLingerTimer?.Stop();
    }
}
