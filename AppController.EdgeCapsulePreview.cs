using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const double EdgeCapsulePreviewTransferStableMilliseconds = 50;
    private const double EdgeCapsulePreviewPointerToleranceDip = 2;
    private const double EdgeCapsulePreviewCorridorToleranceDip = 10;

    private readonly EdgeCapsuleHoverIntentPredictor
        _edgeCapsulePreviewIntentPredictor = new();
    private EdgeCapsulePreviewLayoutSession? _edgeCapsulePreviewSession;
    private string? _edgeCapsulePreviewOutgoingPaperId;
    private string? _edgeCapsulePreviewQueuedTransferPaperId;
    private string? _edgeCapsulePreviewQueuedCloseOwnerPaperId;
    private EdgeCapsulePreviewActivationIntent?
        _edgeCapsulePreviewActivationIntent;
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

    private readonly record struct EdgeCapsulePreviewPointerResolution(
        PaperWindow? Target,
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
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var ownerSize = owner.CurrentEdgeCapsulePreviewSize;
        if (!owner.IsEdgeCapsulePreviewOpen ||
            !ownerSize.HasValue ||
            ownerSize.Value != session.Size ||
            !owner.CanEnterEdgeCapsulePreview)
        {
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
            session = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                basePlan,
                null,
                currentQueueKey,
                session.OwnerPaperId,
                session.Size,
                PaperLayoutDefaults.CapsuleHeight,
                DeepCapsuleGap);
            if (session == null)
            {
                ResetEdgeCapsulePreviewWithoutArrange();
                return basePlan;
            }
            _edgeCapsulePreviewSession = session;
        }

        return EdgeCapsulePreviewLayoutCoordinator.Apply(basePlan, session);
    }

    private void ResetEdgeCapsulePreviewWithoutArrange()
    {
        ReleaseOutgoingEdgeCapsulePreviewContent();
        var session = _edgeCapsulePreviewSession;
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewActivationIntent = null;
        _edgeCapsulePreviewIntentPredictor.Reset();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            owner.SetEdgeCapsulePreviewClosed(animate: false);
            _edgeCapsulePreviewOutgoingPaperId = session.OwnerPaperId;
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
            CancelEdgeCapsulePreviewActivationIntent();
            QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer.Value);
        if (window.EdgeCapsulePreviewPointerCaptureActive)
        {
            ForgetEdgeCapsulePreviewPointerResolution();
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
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
        if (resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
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
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        // Leaving the complete queue corridor is not part of transfer debounce. Close on the next
        // safe dispatcher turn even if the physical pointer is still moving.
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
        string ownerPaperId)
    {
        if (string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                ownerPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = ownerPaperId;
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

                _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
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

                if (owner.CanEnterEdgeCapsulePreview &&
                    ResolveEdgeCapsulePreviewPointer(session, pointer)
                    .CorridorContains)
                {
                    return;
                }

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
                    !ReferenceEquals(current, window) ||
                    !current.CanEnterEdgeCapsulePreview ||
                    !WindowNative.TryGetCursorScreenPosition(out var pointer) ||
                    !current.IsEdgeCapsuleInteractiveAt(pointer) ||
                    (stableAnchor.HasValue &&
                     EdgeCapsulePreviewPointerMovedBeyondTolerance(
                         stableAnchor.Value.Point,
                         pointer,
                         stableAnchor.Value.DpiScaleX,
                         stableAnchor.Value.DpiScaleY)))
                {
                    return;
                }

                var session = _edgeCapsulePreviewSession;
                if (expectedOwnerPaperId == null)
                {
                    // An initial-profile request is valid only for the first preview in a session.
                    // If another request already opened one, its owner sampler starts a transfer.
                    if (session != null)
                    {
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
        if (IsExiting ||
            !window.CanEnterEdgeCapsulePreview ||
            !window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            return;
        }

        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewActivationIntent = null;
        // A newly prepared view is already a WPF tree even before it is parented. Retire the
        // oldest shrinking card first so rapid A -> B -> C browsing never retains three trees.
        ReleaseOutgoingEdgeCapsulePreviewContent();
        var request = window.PrepareEdgeCapsulePreview();
        if (request == null)
        {
            return;
        }

        var basePlan = BuildCurrentEdgeCapsuleQueuePlan();
        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        var next = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
            basePlan,
            _edgeCapsulePreviewSession,
            queueKey,
            window.EdgeCapsulePreviewPaperId,
            request.Size,
            PaperLayoutDefaults.CapsuleHeight,
            DeepCapsuleGap);
        if (next == null)
        {
            return;
        }

        if (!window.SetEdgeCapsulePreviewOpen(
                request,
                animate: true))
        {
            return;
        }

        var previous = _edgeCapsulePreviewSession;
        if (previous != null &&
            !string.Equals(
                previous.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewOutgoingPaperId = previous.OwnerPaperId;
            if (_windows.TryGetValue(previous.OwnerPaperId, out var oldOwner))
            {
                oldOwner.SetEdgeCapsulePreviewClosed(animate: true);
            }
        }

        _edgeCapsulePreviewSession = next;
        RecordEdgeCapsulePreviewTransferPointer(window, next.QueueKey);
        ArrangeDeepCapsules(animate: true);
    }

    private void CloseEdgeCapsulePreview(bool animate, bool arrange)
    {
        ReleaseOutgoingEdgeCapsulePreviewContent();
        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
        var session = _edgeCapsulePreviewSession;
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewActivationIntent = null;
        PaperWindow? owner = null;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var currentOwner))
        {
            owner = currentOwner;
            currentOwner.SetEdgeCapsulePreviewClosed(animate);
            _edgeCapsulePreviewOutgoingPaperId = session.OwnerPaperId;
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

    private void ReleaseOutgoingEdgeCapsulePreviewContent()
    {
        var outgoingPaperId = _edgeCapsulePreviewOutgoingPaperId;
        _edgeCapsulePreviewOutgoingPaperId = null;
        if (outgoingPaperId == null)
        {
            return;
        }

        if (_windows.TryGetValue(outgoingPaperId, out var outgoing))
        {
            outgoing.ClearEdgeCapsulePreviewContent();
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

            if (target == null &&
                !string.Equals(
                    paperId,
                    session.OwnerPaperId,
                    StringComparison.Ordinal) &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer))
            {
                target = window;
            }
        }

        if (!hasBounds)
        {
            return new EdgeCapsulePreviewPointerResolution(target, false);
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
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        _edgeCapsulePreviewIntentPredictor.Reset();
        foreach (var window in _windows.Values)
        {
            window.RefreshEdgeCapsuleHoverIntentSettings();
        }
    }
}
