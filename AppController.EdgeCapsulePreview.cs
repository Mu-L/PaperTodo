using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const double EdgeCapsulePreviewTransferStableMilliseconds = 50;
    private const double EdgeCapsulePreviewPointerToleranceDip = 2;
    private const double EdgeCapsulePreviewCorridorToleranceDip = 10;
    private const double EdgeCapsulePreviewFixedCorridorCloseMilliseconds = 3000;
    private const double EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds = 24;
    private const double EdgeCapsulePreviewCorridorTrackingSettleMilliseconds = 140;

    private readonly EdgeCapsuleHoverIntentPredictor
        _edgeCapsulePreviewIntentPredictor = new();
    private EdgeCapsulePreviewLayoutSession? _edgeCapsulePreviewSession;
    private string? _edgeCapsulePreviewQueuedTransferPaperId;
    private string? _edgeCapsulePreviewQueuedCloseOwnerPaperId;
    private EdgeCapsulePreviewCloseReason _edgeCapsulePreviewQueuedCloseReason;
    private EdgeCapsulePreviewActivationIntent?
        _edgeCapsulePreviewActivationIntent;
    private EdgeCapsulePreviewCorridorExitIntent?
        _edgeCapsulePreviewCorridorExitIntent;
    private DispatcherTimer? _edgeCapsulePreviewCorridorIntentTimer;
    private EdgeCapsulePreviewPointerAnchor?
        _edgeCapsulePreviewLayoutSuppressionAnchor;
    private int _edgeCapsulePreviewTransferGeneration;
    private int _edgeCapsulePreviewCloseGeneration;
    private int _edgeCapsulePreviewPointerResolutionVersion;
    private EdgeCapsulePreviewLayoutSession?
        _edgeCapsulePreviewLastResolvedSession;
    private DeviceScreenPoint? _edgeCapsulePreviewLastResolvedPointer;
    private int _edgeCapsulePreviewLastResolvedVersion = -1;

    private readonly record struct EdgeCapsulePreviewActivationIntent(
        string TargetPaperId,
        string? ExpectedOwnerPaperId,
        DeviceScreenPoint StableAnchor,
        long CandidateSinceTimestamp,
        long StableSinceTimestamp);

    private readonly record struct EdgeCapsulePreviewPointerAnchor(
        DeviceScreenPoint Point,
        double DpiScaleX,
        double DpiScaleY,
        string QueueKey);

    private readonly record struct EdgeCapsulePreviewStableAnchor(
        DeviceScreenPoint Point,
        double DpiScaleX,
        double DpiScaleY);

    private enum EdgeCapsulePreviewCloseReason
    {
        OutsideCorridor,
        CorridorIntent
    }

    private readonly record struct EdgeCapsulePreviewCorridorExitIntent(
        string OwnerPaperId,
        long CorridorSinceTimestamp,
        DeviceScreenPoint StableAnchor,
        long StableSinceTimestamp,
        long? DirectionAwaySinceTimestamp);

    private readonly record struct EdgeCapsulePreviewPointerResolution(
        PaperWindow? Target,
        bool OwnerContains,
        bool CorridorContains);

    private EdgeCapsuleQueuePlan BuildCurrentEdgeCapsuleQueuePlan()
    {
        var papers = DeepCapsulePapersInOrder();
        return EdgeCapsuleQueueCoordinator.Build(
            papers.Select(paper =>
                new EdgeCapsuleQueueMember(paper, QueueKey(paper))),
            State.UseCapsuleCollapseAll);
    }

    private EdgeCapsuleQueuePlan ApplyEdgeCapsulePreviewLayout(
        EdgeCapsuleQueuePlan basePlan)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            return basePlan;
        }

        if (!basePlan.Placements.ContainsKey(session.OwnerPaperId) ||
            IsCapsuleCollapseAllActiveForQueue(session.QueueKey) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            TraceEdgeCapsulePreview(
                $"layout reset owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"reason=owner-missing-or-queue-unavailable queue={session.QueueKey}");
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var ownerSize = owner.CurrentEdgeCapsulePreviewSize;
        if (!owner.IsEdgeCapsulePreviewOpen ||
            !ownerSize.HasValue ||
            ownerSize.Value != session.Size ||
            !owner.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"layout reset owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"reason=owner-state open={owner.IsEdgeCapsulePreviewOpen} " +
                $"size={(ownerSize.HasValue ? ownerSize.Value.ToString() : "<null>")} " +
                $"expectedSize={session.Size} eligibility={owner.EdgeCapsulePreviewEligibilityTrace()}");
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var currentQueueKey = QueueKey(owner.EdgeCapsulePreviewPaper);
        var currentQueue = basePlan.Queues.FirstOrDefault(queue =>
            string.Equals(
                queue.Key,
                currentQueueKey,
                StringComparison.Ordinal));
        if (currentQueue == null)
        {
            TraceEdgeCapsulePreview(
                $"layout reset owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"reason=current-queue-missing queue={currentQueueKey}");
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var currentIds = currentQueue.Papers
            .Select(paper => paper.Id)
            .ToArray();
        if (!string.Equals(
                session.QueueKey,
                currentQueueKey,
                StringComparison.Ordinal) ||
            !session.QueuePaperIds.SequenceEqual(
                currentIds,
                StringComparer.Ordinal))
        {
            TraceEdgeCapsulePreview(
                $"layout refresh owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"queue={session.QueueKey}->{currentQueueKey}");
            session = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                basePlan,
                currentQueueKey,
                session.OwnerPaperId,
                session.Size,
                PaperLayoutDefaults.CapsuleHeight);
            if (session == null)
            {
                TraceEdgeCapsulePreview(
                    $"layout reset owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)} " +
                    "reason=layout-refresh-failed");
                ResetEdgeCapsulePreviewWithoutArrange();
                return basePlan;
            }
            _edgeCapsulePreviewSession = session;
        }

        return EdgeCapsulePreviewLayoutCoordinator.Apply(basePlan, session);
    }

    private void ResetEdgeCapsulePreviewWithoutArrange()
    {
        var session = _edgeCapsulePreviewSession;
        TraceEdgeCapsulePreview(
            $"reset without arrange owner={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)}");
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideCorridor;
        _edgeCapsulePreviewActivationIntent = null;
        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            owner.SetEdgeCapsulePreviewClosed(animate: false);
        }
    }

    internal void NotifyEdgeCapsulePointerOverChanged(
        PaperWindow window,
        bool pointerOver)
    {
        if (IsExiting)
        {
            return;
        }

        if (!pointerOver)
        {
            CancelEdgeCapsulePreviewActivationIntent(
                window.EdgeCapsulePreviewPaperId);
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"enter blocked target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"eligibility={window.EdgeCapsulePreviewEligibilityTrace()} " +
                $"owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)}");
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null &&
            string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer) ||
            !window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer);
        if (IsEdgeCapsulePreviewLayoutSuppressedFor(window))
        {
            // A WPF enter caused only by the moving queue has no activation authority. Real
            // screen-space pointer motion clears this suppression before the intent gate starts.
            TraceEdgeCapsulePreview(
                $"enter suppressed target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"owner={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)} pointer={pointer.X},{pointer.Y}");
            return;
        }

        AdvanceEdgeCapsulePreviewActivationIntent(
            session,
            window,
            pointer);
    }

    internal void NotifyEdgeCapsulePreviewPointerSample(
        PaperWindow window,
        DeviceScreenPoint? pointer)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            if (!pointer.HasValue)
            {
                return;
            }

            ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
                pointer.Value);
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(window))
            {
                CancelEdgeCapsulePreviewActivationIntent(
                    window.EdgeCapsulePreviewPaperId);
                return;
            }

            // This also recovers an initial enter whose first dispatcher turn became stale. The
            // initial profile is permissive, but still observes real motion before allowing a fast
            // bottom-to-top sweep to open the first crossed capsule.
            if (window.IsEdgeCapsulePointerOver &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer.Value))
            {
                AdvanceEdgeCapsulePreviewActivationIntent(
                    null,
                    window,
                    pointer.Value);
            }
            else
            {
                CancelEdgeCapsulePreviewActivationIntent(
                    window.EdgeCapsulePreviewPaperId);
            }
            return;
        }

        if (!string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal) ||
            !pointer.HasValue)
        {
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"owner sample blocked owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"eligibility={window.EdgeCapsulePreviewEligibilityTrace()}");
            CancelEdgeCapsulePreviewActivationIntent();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer.Value);
        if (window.EdgeCapsulePreviewPointerCaptureActive)
        {
            ForgetEdgeCapsulePreviewPointerResolution();
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            return;
        }

        if (CanReuseEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value))
        {
            return;
        }

        ObserveEdgeCapsulePreviewPointer(window, pointer.Value);

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (resolution.OwnerContains)
        {
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        if (resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(
                    resolution.Target))
            {
                CancelEdgeCapsulePreviewActivationIntent();
                RememberEdgeCapsulePreviewPointerResolution(
                    session,
                    pointer.Value);
                return;
            }

            AdvanceEdgeCapsulePreviewActivationIntent(
                session,
                resolution.Target,
                pointer.Value);
            return;
        }

        CancelEdgeCapsulePreviewActivationIntent();
        if (resolution.CorridorContains)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            AdvanceEdgeCapsulePreviewCorridorExitIntent(
                window,
                session,
                pointer.Value);
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        // Leaving the complete queue corridor is not part of transfer debounce. Close on the next
        // safe dispatcher turn even if the physical pointer is still moving.
        ResetEdgeCapsulePreviewCorridorExitIntent();
        RememberEdgeCapsulePreviewPointerResolution(
            session,
            pointer.Value);
        QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
    }

    internal bool CloseEdgeCapsulePreviewForDrag(PaperWindow draggedWindow)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            return false;
        }

        var draggedWindowWasOwner = string.Equals(
            session.OwnerPaperId,
            draggedWindow.EdgeCapsulePreviewPaperId,
            StringComparison.Ordinal);
        _windows.TryGetValue(session.OwnerPaperId, out var owner);
        CloseEdgeCapsulePreview(animate: false, arrange: true);
        owner?.FlushEdgeCapsulePreviewCompactPresentation();
        return draggedWindowWasOwner;
    }

    internal void CloseEdgeCapsulePreviewForClose(PaperWindow window)
    {
        if (IsEdgeCapsulePreviewOwner(window))
        {
            CloseEdgeCapsulePreview(animate: false, arrange: false);
        }
    }

    internal bool IsEdgeCapsulePreviewOwner(PaperWindow window) =>
        _edgeCapsulePreviewSession is { } session &&
        string.Equals(
            session.OwnerPaperId,
            window.EdgeCapsulePreviewPaperId,
            StringComparison.Ordinal);

    private void QueueEdgeCapsulePreviewClose(
        PaperWindow window,
        string ownerPaperId,
        EdgeCapsulePreviewCloseReason reason =
            EdgeCapsulePreviewCloseReason.OutsideCorridor)
    {
        if (string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                ownerPaperId,
                StringComparison.Ordinal))
        {
            if (reason == EdgeCapsulePreviewCloseReason.CorridorIntent)
            {
                _edgeCapsulePreviewQueuedCloseReason = reason;
            }
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = ownerPaperId;
        _edgeCapsulePreviewQueuedCloseReason = reason;
        var generation = ++_edgeCapsulePreviewCloseGeneration;
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _edgeCapsulePreviewCloseGeneration ||
                    !string.Equals(
                        _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                var queuedReason = _edgeCapsulePreviewQueuedCloseReason;
                _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
                _edgeCapsulePreviewQueuedCloseReason =
                    EdgeCapsulePreviewCloseReason.OutsideCorridor;
                if (IsExiting ||
                    _edgeCapsulePreviewSession is not { } session ||
                    !string.Equals(
                        session.OwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(ownerPaperId, out var owner) ||
                    !ReferenceEquals(owner, window) ||
                    (owner.CanEnterEdgeCapsulePreview &&
                     owner.EdgeCapsulePreviewPointerCaptureActive) ||
                    !WindowNative.TryGetCursorScreenPosition(out var pointer))
                {
                    return;
                }

                var resolution = ResolveEdgeCapsulePreviewPointer(
                    session,
                    pointer);
                if (owner.CanEnterEdgeCapsulePreview &&
                    (resolution.OwnerContains || resolution.Target != null))
                {
                    return;
                }
                if (owner.CanEnterEdgeCapsulePreview &&
                    queuedReason ==
                        EdgeCapsulePreviewCloseReason.OutsideCorridor &&
                    resolution.CorridorContains)
                {
                    return;
                }

                TraceEdgeCapsulePreview(
                    $"close queued owner={EdgeCapsulePreviewTraceId(ownerPaperId)} " +
                    $"reason={queuedReason} pointer={pointer.X},{pointer.Y}");
                CloseEdgeCapsulePreview(animate: true, arrange: true);
            }),
            DispatcherPriority.Input);
    }

    private void CancelQueuedEdgeCapsulePreviewClose()
    {
        if (_edgeCapsulePreviewQueuedCloseOwnerPaperId == null)
        {
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideCorridor;
        _edgeCapsulePreviewCloseGeneration++;
    }

    private void QueueEdgeCapsulePreviewTransfer(
        PaperWindow window,
        string? expectedOwnerPaperId = null,
        EdgeCapsulePreviewStableAnchor? stableAnchor = null)
    {
        var paperId = window.EdgeCapsulePreviewPaperId;
        if (string.Equals(
                _edgeCapsulePreviewQueuedTransferPaperId,
                paperId,
                StringComparison.Ordinal))
        {
            return;
        }

        _edgeCapsulePreviewQueuedTransferPaperId = paperId;
        var generation = ++_edgeCapsulePreviewTransferGeneration;
        TraceEdgeCapsulePreview(
            $"transfer queued target={EdgeCapsulePreviewTraceId(paperId)} " +
            $"expectedOwner={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} generation={generation}");
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (IsExiting ||
                    generation != _edgeCapsulePreviewTransferGeneration ||
                    !string.Equals(
                        _edgeCapsulePreviewQueuedTransferPaperId,
                        paperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                _edgeCapsulePreviewQueuedTransferPaperId = null;
                if (!_windows.TryGetValue(paperId, out var current) ||
                    !ReferenceEquals(current, window))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} reason=window-changed");
                    return;
                }
                if (!current.CanEnterEdgeCapsulePreview)
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=blocked eligibility={current.EdgeCapsulePreviewEligibilityTrace()}");
                    return;
                }
                if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} reason=no-pointer");
                    return;
                }
                if (!current.IsEdgeCapsuleInteractiveAt(pointer))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=pointer-outside pointer={pointer.X},{pointer.Y}");
                    return;
                }
                if (stableAnchor.HasValue &&
                    EdgeCapsulePreviewPointerMovedBeyondTolerance(
                        stableAnchor.Value.Point,
                        pointer,
                        stableAnchor.Value.DpiScaleX,
                        stableAnchor.Value.DpiScaleY))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=anchor-moved anchor={stableAnchor.Value.Point.X},{stableAnchor.Value.Point.Y} " +
                        $"pointer={pointer.X},{pointer.Y}");
                    return;
                }

                var session = _edgeCapsulePreviewSession;
                if (expectedOwnerPaperId == null)
                {
                    // An initial-profile request is valid only for the first preview in a session.
                    // If another request already opened one, its owner sampler starts a transfer.
                    if (session != null)
                    {
                        TraceEdgeCapsulePreview(
                            $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                            $"reason=session-already-open owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)}");
                        return;
                    }
                }
                else if (session == null ||
                    !string.Equals(
                        session.OwnerPaperId,
                        expectedOwnerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(
                        expectedOwnerPaperId,
                        out var owner) ||
                    owner.EdgeCapsulePreviewPointerCaptureActive)
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=owner-changed expected={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} " +
                        $"actual={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)}");
                    return;
                }

                OpenOrTransferEdgeCapsulePreview(current, pointer);
            }),
            DispatcherPriority.Loaded);
    }

    private void OpenOrTransferEdgeCapsulePreview(
        PaperWindow window,
        DeviceScreenPoint pointer)
    {
        if (IsExiting)
        {
            return;
        }
        if (!window.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"open blocked target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"eligibility={window.EdgeCapsulePreviewEligibilityTrace()}");
            return;
        }
        if (!window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            TraceEdgeCapsulePreview(
                $"open blocked target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"reason=pointer-outside pointer={pointer.X},{pointer.Y}");
            return;
        }

        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideCorridor;
        _edgeCapsulePreviewActivationIntent = null;
        var request = window.PrepareEdgeCapsulePreview();
        if (request == null)
        {
            TraceEdgeCapsulePreview(
                $"open aborted target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                "reason=prepare-null");
            return;
        }

        var basePlan = BuildCurrentEdgeCapsuleQueuePlan();
        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        var next = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
            basePlan,
            queueKey,
            window.EdgeCapsulePreviewPaperId,
            request.Size,
            PaperLayoutDefaults.CapsuleHeight);
        if (next == null)
        {
            TraceEdgeCapsulePreview(
                $"open aborted target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"reason=layout-null queue={queueKey}");
            return;
        }

        // Commit the controller owner before the target view is mounted. StagePreviewContent and
        // WPF layout are allowed to re-enter input/layout code; every re-entrant observer must see
        // the same owner that the target model is about to expose. If opening is rejected, roll
        // the controller session back before returning.
        var previous = _edgeCapsulePreviewSession;
        _edgeCapsulePreviewSession = next;
        TraceEdgeCapsulePreview(
            $"session switch prepare from={EdgeCapsulePreviewTraceId(previous?.OwnerPaperId)} " +
            $"to={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} queue={next.QueueKey}");
        if (!window.SetEdgeCapsulePreviewOpen(
                request,
                animate: true))
        {
            _edgeCapsulePreviewSession = previous;
            TraceEdgeCapsulePreview(
                $"session switch rollback target={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} " +
                $"restore={EdgeCapsulePreviewTraceId(previous?.OwnerPaperId)} reason=model-rejected");
            return;
        }

        if (previous != null &&
            !string.Equals(
                previous.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            if (_windows.TryGetValue(previous.OwnerPaperId, out var oldOwner))
            {
                oldOwner.SetEdgeCapsulePreviewClosed(animate: true);
            }
        }

        ResetEdgeCapsulePreviewCorridorExitIntent();
        RecordEdgeCapsulePreviewTransferPointer(window, next.QueueKey);
        ArrangeDeepCapsules(animate: true);
        var displaced = string.Join(
            ",",
            next.TopOffsetsDip
                .Where(pair => Math.Abs(pair.Value) > 0.01)
                .Select(pair => $"{EdgeCapsulePreviewTraceId(pair.Key)}:{pair.Value:F1}"));
        TraceEdgeCapsulePreview(
            $"session switch committed owner={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} " +
            $"displaced={(string.IsNullOrEmpty(displaced) ? "<none>" : displaced)}");
    }

    private void CloseEdgeCapsulePreview(bool animate, bool arrange)
    {
        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
        var session = _edgeCapsulePreviewSession;
        TraceEdgeCapsulePreview(
            $"close owner={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)} " +
            $"animate={animate} arrange={arrange}");
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideCorridor;
        _edgeCapsulePreviewActivationIntent = null;
        ResetEdgeCapsulePreviewCorridorExitIntent();
        PaperWindow? owner = null;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var currentOwner))
        {
            owner = currentOwner;
            currentOwner.SetEdgeCapsulePreviewClosed(animate);
        }

        if (arrange && session != null && owner != null)
        {
            // Compacting the source queue must not manufacture a new hover under a stationary
            // pointer. The queue key scopes this suppression so a capsule already reached in a
            // different queue can still become an initial candidate immediately.
            RecordEdgeCapsulePreviewTransferPointer(owner, session.QueueKey);
        }
        else
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
        }

        if (arrange)
        {
            ArrangeDeepCapsules(animate);
        }
    }

    private void RecordEdgeCapsulePreviewTransferPointer(
        PaperWindow target,
        string queueKey)
    {
        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        double dpiScaleX;
        double dpiScaleY;
        if (target.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
        {
            dpiScaleX = geometry.DpiScaleX;
            dpiScaleY = geometry.DpiScaleY;
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
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        _edgeCapsulePreviewLayoutSuppressionAnchor =
            new EdgeCapsulePreviewPointerAnchor(
                pointer,
                dpiScaleX,
                dpiScaleY,
                queueKey);
        _edgeCapsulePreviewIntentPredictor.Reset(
            pointer,
            Stopwatch.GetTimestamp(),
            dpiScaleX,
            dpiScaleY);
    }

    private void ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
        DeviceScreenPoint pointer)
    {
        if (!_edgeCapsulePreviewLayoutSuppressionAnchor.HasValue)
        {
            return;
        }

        var anchor = _edgeCapsulePreviewLayoutSuppressionAnchor.Value;
        if (EdgeCapsulePreviewPointerMovedBeyondTolerance(
                anchor.Point,
                pointer,
                anchor.DpiScaleX,
                anchor.DpiScaleY))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        }
    }

    private bool IsEdgeCapsulePreviewLayoutSuppressedFor(
        PaperWindow target)
    {
        return _edgeCapsulePreviewLayoutSuppressionAnchor is { } anchor &&
            string.Equals(
                anchor.QueueKey,
                QueueKey(target.EdgeCapsulePreviewPaper),
                StringComparison.Ordinal);
    }

    private void AdvanceEdgeCapsulePreviewActivationIntent(
        EdgeCapsulePreviewLayoutSession? session,
        PaperWindow target,
        DeviceScreenPoint pointer)
    {
        var targetPaperId = target.EdgeCapsulePreviewPaperId;
        if (!target.TryGetEdgeCapsuleInteractiveGeometry(
                out var targetGeometry))
        {
            CancelEdgeCapsulePreviewActivationIntent(targetPaperId);
            return;
        }

        var expectedOwnerPaperId = session?.OwnerPaperId;
        var now = Stopwatch.GetTimestamp();
        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        if (predictiveIntentEnabled && session == null)
        {
            // Transfers already sampled this physical frame through the current owner's shared
            // scheduler, including time spent in queue gaps. Initial activation has no owner.
            _edgeCapsulePreviewIntentPredictor.Observe(
                pointer,
                now,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY);
        }
        else if (session == null)
        {
            // The legacy behavior has no dwell for the first card. Only transfers use its fixed
            // 50 ms / 2 DIP stability gate. Do not cancel an identical queued Loaded callback on
            // every render frame; QueueEdgeCapsulePreviewTransfer already coalesces that target.
            _edgeCapsulePreviewActivationIntent = null;
            QueueEdgeCapsulePreviewTransfer(target);
            return;
        }

        var intent = _edgeCapsulePreviewActivationIntent;
        if (!intent.HasValue ||
            !string.Equals(
                intent.Value.TargetPaperId,
                targetPaperId,
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.Value.ExpectedOwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewActivationIntent =
                new EdgeCapsulePreviewActivationIntent(
                    targetPaperId,
                    expectedOwnerPaperId,
                    pointer,
                    now,
                    now);
            TraceEdgeCapsulePreview(
                $"intent candidate target={EdgeCapsulePreviewTraceId(targetPaperId)} " +
                $"owner={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} pointer={pointer.X},{pointer.Y}");
            return;
        }

        var currentIntent = intent.Value;
        if (EdgeCapsulePreviewPointerMovedBeyondTolerance(
                currentIntent.StableAnchor,
                pointer,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY))
        {
            currentIntent = currentIntent with
            {
                StableAnchor = pointer,
                StableSinceTimestamp = now
            };
        }

        _edgeCapsulePreviewActivationIntent = currentIntent;
        var candidateElapsed = Stopwatch.GetElapsedTime(
            currentIntent.CandidateSinceTimestamp,
            now).TotalMilliseconds;
        var stableElapsed = Stopwatch.GetElapsedTime(
            currentIntent.StableSinceTimestamp,
            now).TotalMilliseconds;
        if (!predictiveIntentEnabled)
        {
            if (stableElapsed < EdgeCapsulePreviewTransferStableMilliseconds)
            {
                return;
            }
        }
        else
        {
            var decision = _edgeCapsulePreviewIntentPredictor.Evaluate(
                session == null
                    ? EdgeCapsuleHoverIntentMode.Initial
                    : EdgeCapsuleHoverIntentMode.Transfer,
                State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                targetGeometry.Bounds,
                pointer,
                candidateElapsed,
                stableElapsed);
            if (decision != EdgeCapsuleHoverIntentDecision.NoExtraDelay)
            {
                return;
            }
        }

        _edgeCapsulePreviewActivationIntent = null;
        TraceEdgeCapsulePreview(
            $"intent accepted target={EdgeCapsulePreviewTraceId(targetPaperId)} " +
            $"owner={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} " +
            $"candidateMs={candidateElapsed:F1} stableMs={stableElapsed:F1}");
        QueueEdgeCapsulePreviewTransfer(
            target,
            expectedOwnerPaperId,
            new EdgeCapsulePreviewStableAnchor(
                currentIntent.StableAnchor,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY));
    }

    internal void InvalidateEdgeCapsulePreviewPointerResolution()
    {
        unchecked
        {
            _edgeCapsulePreviewPointerResolutionVersion++;
        }
        ForgetEdgeCapsulePreviewPointerResolution();
    }

    private bool CanReuseEdgeCapsulePreviewPointerResolution(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer) =>
        // Rendering still advances at the monitor's native refresh rate. Only an unchanged,
        // settled hit-test result is reused; pending intent and every presentation/input change
        // continue through the full physical queue resolver.
        _edgeCapsulePreviewActivationIntent == null &&
        _edgeCapsulePreviewQueuedTransferPaperId == null &&
        ReferenceEquals(
            _edgeCapsulePreviewLastResolvedSession,
            session) &&
        _edgeCapsulePreviewLastResolvedPointer == pointer &&
        _edgeCapsulePreviewLastResolvedVersion ==
            _edgeCapsulePreviewPointerResolutionVersion;

    private void RememberEdgeCapsulePreviewPointerResolution(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        _edgeCapsulePreviewLastResolvedSession = session;
        _edgeCapsulePreviewLastResolvedPointer = pointer;
        _edgeCapsulePreviewLastResolvedVersion =
            _edgeCapsulePreviewPointerResolutionVersion;
    }

    private void ForgetEdgeCapsulePreviewPointerResolution()
    {
        _edgeCapsulePreviewLastResolvedSession = null;
        _edgeCapsulePreviewLastResolvedPointer = null;
        _edgeCapsulePreviewLastResolvedVersion = -1;
    }

    private void ObserveEdgeCapsulePreviewPointer(
        PaperWindow owner,
        DeviceScreenPoint pointer)
    {
        if (!State.ExperimentalEdgeCapsuleHoverIntent)
        {
            return;
        }

        double dpiScaleX;
        double dpiScaleY;
        if (owner.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
        {
            dpiScaleX = geometry.DpiScaleX;
            dpiScaleY = geometry.DpiScaleY;
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
            return;
        }

        _edgeCapsulePreviewIntentPredictor.Observe(
            pointer,
            Stopwatch.GetTimestamp(),
            dpiScaleX,
            dpiScaleY);
    }

    private void AdvanceEdgeCapsulePreviewCorridorExitIntent(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        var now = Stopwatch.GetTimestamp();
        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var intent = _edgeCapsulePreviewCorridorExitIntent;
        var current = !intent.HasValue ||
            !string.Equals(
                intent.Value.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal)
            ? new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                pointer,
                now,
                null)
            : intent.Value;

        if (predictiveIntentEnabled)
        {
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
                ResetEdgeCapsulePreviewCorridorExitIntent();
                return;
            }

            if (EdgeCapsulePreviewPointerMovedBeyondTolerance(
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
        }

        var corridorElapsed = Stopwatch.GetElapsedTime(
            current.CorridorSinceTimestamp,
            now).TotalMilliseconds;
        var directionAwayElapsed = current.DirectionAwaySinceTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(
                current.DirectionAwaySinceTimestamp.Value,
                now).TotalMilliseconds
            : 0;
        var stableElapsed = Stopwatch.GetElapsedTime(
            current.StableSinceTimestamp,
            now).TotalMilliseconds;

        EdgeCapsuleCorridorExitDecision decision;
        if (!predictiveIntentEnabled)
        {
            decision = corridorElapsed >=
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

            decision = _edgeCapsulePreviewIntentPredictor
                .EvaluateCorridorExit(
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                    keepAliveBounds.Slice(0, keepAliveCount),
                    pointer,
                    directionAwayElapsed,
                    stableElapsed);
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
                    $"corridor close owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                    $"reason={decision} pointer={pointer.X},{pointer.Y} " +
                    $"corridorMs={corridorElapsed:F1} " +
                    $"awayMs={directionAwayElapsed:F1} stableMs={stableElapsed:F1}");
                QueueEdgeCapsulePreviewClose(
                    owner,
                    session.OwnerPaperId,
                    EdgeCapsulePreviewCloseReason.CorridorIntent);
                ResetEdgeCapsulePreviewCorridorExitIntent();
                return;
            default:
                current = current with
                {
                    DirectionAwaySinceTimestamp = null
                };
                break;
        }

        _edgeCapsulePreviewCorridorExitIntent = current;
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(owner, now);
    }

    private void ScheduleEdgeCapsulePreviewCorridorIntentCheck(
        PaperWindow owner,
        long now)
    {
        if (_edgeCapsulePreviewCorridorExitIntent is not { } intent)
        {
            return;
        }

        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var idleCloseMilliseconds = predictiveIntentEnabled
            ? _edgeCapsulePreviewIntentPredictor
                .CorridorIdleCloseMilliseconds(
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity)
            : EdgeCapsulePreviewFixedCorridorCloseMilliseconds;
        var stableElapsed = Stopwatch.GetElapsedTime(
            intent.StableSinceTimestamp,
            now).TotalMilliseconds;
        var corridorElapsed = Stopwatch.GetElapsedTime(
            intent.CorridorSinceTimestamp,
            now).TotalMilliseconds;
        var closeElapsed = predictiveIntentEnabled
            ? stableElapsed
            : corridorElapsed;
        var remaining = Math.Max(1, idleCloseMilliseconds - closeElapsed);
        // Empty corridor pixels are intentionally HTTRANSPARENT, so they do not keep producing WPF
        // mouse moves. Predictive mode samples briefly while motion is still present, then switches
        // to one idle deadline; fixed mode always schedules only its single corridor deadline.
        var nextCheck = predictiveIntentEnabled && stableElapsed <
            EdgeCapsulePreviewCorridorTrackingSettleMilliseconds
            ? Math.Min(
                EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds,
                remaining)
            : remaining;
        if (_edgeCapsulePreviewCorridorIntentTimer == null)
        {
            _edgeCapsulePreviewCorridorIntentTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                owner.Dispatcher);
            _edgeCapsulePreviewCorridorIntentTimer.Tick +=
                OnEdgeCapsulePreviewCorridorIntentTimerTick;
        }

        _edgeCapsulePreviewCorridorIntentTimer.Stop();
        _edgeCapsulePreviewCorridorIntentTimer.Interval =
            TimeSpan.FromMilliseconds(nextCheck);
        _edgeCapsulePreviewCorridorIntentTimer.Start();
    }

    private void OnEdgeCapsulePreviewCorridorIntentTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewCorridorIntentTimer?.Stop();
        if (IsExiting ||
            _edgeCapsulePreviewCorridorExitIntent is not { } intent ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                intent.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive ||
            !WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            return;
        }

        ObserveEdgeCapsulePreviewPointer(owner, pointer);
        var resolution = ResolveEdgeCapsulePreviewPointer(session, pointer);
        if (resolution.OwnerContains || resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            return;
        }
        if (!resolution.CorridorContains)
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            QueueEdgeCapsulePreviewClose(owner, session.OwnerPaperId);
            return;
        }

        AdvanceEdgeCapsulePreviewCorridorExitIntent(
            owner,
            session,
            pointer);
    }

    private void ResetEdgeCapsulePreviewCorridorExitIntent()
    {
        _edgeCapsulePreviewCorridorExitIntent = null;
        _edgeCapsulePreviewCorridorIntentTimer?.Stop();
    }

    private void CancelEdgeCapsulePreviewActivationIntent(
        string? targetPaperId = null)
    {
        var intent = _edgeCapsulePreviewActivationIntent;
        if (intent.HasValue &&
            (targetPaperId == null ||
             string.Equals(
                 intent.Value.TargetPaperId,
                 targetPaperId,
                 StringComparison.Ordinal)))
        {
            _edgeCapsulePreviewActivationIntent = null;
        }

        if (_edgeCapsulePreviewQueuedTransferPaperId != null &&
            (targetPaperId == null ||
             string.Equals(
                 _edgeCapsulePreviewQueuedTransferPaperId,
                 targetPaperId,
                 StringComparison.Ordinal)))
        {
            _edgeCapsulePreviewQueuedTransferPaperId = null;
            _edgeCapsulePreviewTransferGeneration++;
        }
    }

    private EdgeCapsulePreviewPointerResolution ResolveEdgeCapsulePreviewPointer(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        PaperWindow? target = null;
        var ownerContains = false;
        var hasBounds = false;
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        var dpiScaleX = 1.0;
        var dpiScaleY = 1.0;
        var hasQueueDpi = false;

        foreach (var paperId in session.QueuePaperIds)
        {
            if (!_windows.TryGetValue(paperId, out var window))
            {
                continue;
            }

            if (window.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
            {
                var bounds = geometry.Bounds;
                hasBounds = true;
                left = Math.Min(left, bounds.Left);
                top = Math.Min(top, bounds.Top);
                right = Math.Max(right, bounds.Right);
                bottom = Math.Max(bottom, bounds.Bottom);
                if (!hasQueueDpi ||
                    string.Equals(
                        paperId,
                        session.OwnerPaperId,
                        StringComparison.Ordinal))
                {
                    dpiScaleX = NormalizeEdgeCapsulePreviewDpiScale(
                        geometry.DpiScaleX);
                    dpiScaleY = NormalizeEdgeCapsulePreviewDpiScale(
                        geometry.DpiScaleY);
                    hasQueueDpi = true;
                }
            }

            var isOwner = string.Equals(
                paperId,
                session.OwnerPaperId,
                StringComparison.Ordinal);
            if (isOwner)
            {
                ownerContains = window.IsEdgeCapsuleInteractiveAt(pointer);
            }
            else if (target == null &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer))
            {
                target = window;
            }
        }

        if (!hasBounds)
        {
            return new EdgeCapsulePreviewPointerResolution(
                target,
                ownerContains,
                false);
        }

        var horizontalTolerance = (int)Math.Ceiling(
            EdgeCapsulePreviewCorridorToleranceDip * dpiScaleX);
        var verticalTolerance = (int)Math.Ceiling(
            EdgeCapsulePreviewCorridorToleranceDip * dpiScaleY);
        var corridorContains =
            pointer.X >= left - horizontalTolerance &&
            pointer.X < right + horizontalTolerance &&
            pointer.Y >= top - verticalTolerance &&
            pointer.Y < bottom + verticalTolerance;
        return new EdgeCapsulePreviewPointerResolution(
            target,
            ownerContains,
            corridorContains);
    }

    private static bool EdgeCapsulePreviewPointerMovedBeyondTolerance(
        DeviceScreenPoint anchor,
        DeviceScreenPoint pointer,
        double dpiScaleX,
        double dpiScaleY)
    {
        var deltaX = (pointer.X - anchor.X) /
            NormalizeEdgeCapsulePreviewDpiScale(dpiScaleX);
        var deltaY = (pointer.Y - anchor.Y) /
            NormalizeEdgeCapsulePreviewDpiScale(dpiScaleY);
        var tolerance = EdgeCapsulePreviewPointerToleranceDip;
        return deltaX * deltaX + deltaY * deltaY >
            tolerance * tolerance;
    }

    private static double NormalizeEdgeCapsulePreviewDpiScale(double scale) =>
        double.IsFinite(scale) ? Math.Max(1, scale) : 1;

    private void RefreshEdgeCapsuleHoverIntentRuntime()
    {
        CancelEdgeCapsulePreviewActivationIntent();
        CancelQueuedEdgeCapsulePreviewClose();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        _edgeCapsulePreviewIntentPredictor.Reset();
        foreach (var window in _windows.Values)
        {
            window.RefreshEdgeCapsuleHoverIntentSettings();
        }
    }
}
