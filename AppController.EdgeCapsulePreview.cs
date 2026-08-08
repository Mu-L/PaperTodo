using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const int EdgeCapsulePreviewTransferStableMilliseconds = 50;
    private const int EdgeCapsulePreviewPointerToleranceDevice = 2;
    private const int EdgeCapsulePreviewCorridorToleranceDevice = 10;

    private EdgeCapsulePreviewLayoutSession? _edgeCapsulePreviewSession;
    private string? _edgeCapsulePreviewOutgoingPaperId;
    private string? _edgeCapsulePreviewQueuedTransferPaperId;
    private string? _edgeCapsulePreviewQueuedCloseOwnerPaperId;
    private EdgeCapsulePreviewTransferIntent? _edgeCapsulePreviewTransferIntent;
    private DeviceScreenPoint? _edgeCapsulePreviewLastTransferPointer;
    private int _edgeCapsulePreviewTransferGeneration;
    private int _edgeCapsulePreviewCloseGeneration;

    private readonly record struct EdgeCapsulePreviewTransferIntent(
        string TargetPaperId,
        DeviceScreenPoint Anchor,
        long StableSinceTimestamp);

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
        _edgeCapsulePreviewTransferIntent = null;
        _edgeCapsulePreviewLastTransferPointer = null;
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
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null)
        {
            if (string.Equals(
                    session.OwnerPaperId,
                    window.EdgeCapsulePreviewPaperId,
                    StringComparison.Ordinal))
            {
                return;
            }

            // Enter only wakes this presenter's shared-frame pointer tracking. The current preview
            // owner resolves the physical target and applies the transfer stability gate.
            return;
        }

        // Use the next Dispatcher turn only to avoid re-entering pointer reconciliation. There is
        // no hover dwell: the shell transition starts as soon as that zero-delay callback runs.
        QueueEdgeCapsulePreviewTransfer(window);
    }

    internal void NotifyEdgeCapsulePreviewPointerSample(
        PaperWindow window,
        DeviceScreenPoint? pointer)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            // This also recovers an initial enter whose dispatcher callback became stale while the
            // capsule remained physically hovered. Initial activation intentionally has no dwell.
            if (pointer.HasValue &&
                window.IsEdgeCapsulePointerOver &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer.Value))
            {
                QueueEdgeCapsulePreviewTransfer(window);
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
            _edgeCapsulePreviewTransferIntent = null;
            QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer.Value);
        if (window.EdgeCapsulePreviewInteractionActive)
        {
            _edgeCapsulePreviewTransferIntent = null;
            CancelQueuedEdgeCapsulePreviewClose();
            return;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            if (_edgeCapsulePreviewLastTransferPointer.HasValue)
            {
                _edgeCapsulePreviewTransferIntent = null;
                return;
            }

            AdvanceEdgeCapsulePreviewTransferIntent(
                session,
                resolution.Target,
                pointer.Value);
            return;
        }

        _edgeCapsulePreviewTransferIntent = null;
        if (resolution.CorridorContains)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            return;
        }

        // Leaving the complete queue corridor is not part of transfer debounce. Close on the next
        // safe dispatcher turn even if the physical pointer is still moving.
        QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
    }

    internal bool CloseEdgeCapsulePreviewForDrag(PaperWindow window)
    {
        if (!IsEdgeCapsulePreviewOwner(window))
        {
            return false;
        }

        CloseEdgeCapsulePreview(animate: false, arrange: true);
        window.FlushEdgeCapsulePreviewCompactPresentation();
        return true;
    }

    internal void CloseEdgeCapsulePreviewForActivation(PaperWindow window)
    {
        if (!IsEdgeCapsulePreviewOwner(window))
        {
            return;
        }

        CloseEdgeCapsulePreview(animate: false, arrange: true);
        window.FlushEdgeCapsulePreviewCompactPresentation();
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
                     owner.EdgeCapsulePreviewInteractionActive) ||
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
        DeviceScreenPoint? stablePointerAnchor = null)
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
                    (stablePointerAnchor.HasValue &&
                     EdgeCapsulePreviewPointerMovedBeyondTolerance(
                         stablePointerAnchor.Value,
                         pointer)))
                {
                    return;
                }

                var session = _edgeCapsulePreviewSession;
                if (expectedOwnerPaperId == null)
                {
                    // A zero-dwell request is valid only for the first preview in a session. If a
                    // different request already opened one, the owner sampler will apply dwell.
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
                    owner.EdgeCapsulePreviewInteractionActive)
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
        _edgeCapsulePreviewTransferIntent = null;
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
        RecordEdgeCapsulePreviewTransferPointer();
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
        _edgeCapsulePreviewTransferIntent = null;
        _edgeCapsulePreviewLastTransferPointer = null;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            owner.SetEdgeCapsulePreviewClosed(animate);
            _edgeCapsulePreviewOutgoingPaperId = session.OwnerPaperId;
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

    private void RecordEdgeCapsulePreviewTransferPointer()
    {
        _edgeCapsulePreviewLastTransferPointer =
            WindowNative.TryGetCursorScreenPosition(out var pointer)
                ? pointer
                : null;
    }

    private void ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
        DeviceScreenPoint pointer)
    {
        if (!_edgeCapsulePreviewLastTransferPointer.HasValue)
        {
            return;
        }

        var anchor = _edgeCapsulePreviewLastTransferPointer.Value;
        if (EdgeCapsulePreviewPointerMovedBeyondTolerance(anchor, pointer))
        {
            _edgeCapsulePreviewLastTransferPointer = null;
        }
    }

    private void AdvanceEdgeCapsulePreviewTransferIntent(
        EdgeCapsulePreviewLayoutSession session,
        PaperWindow target,
        DeviceScreenPoint pointer)
    {
        var targetPaperId = target.EdgeCapsulePreviewPaperId;
        var now = Stopwatch.GetTimestamp();
        var intent = _edgeCapsulePreviewTransferIntent;
        if (!intent.HasValue ||
            !string.Equals(
                intent.Value.TargetPaperId,
                targetPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewTransferIntent =
                new EdgeCapsulePreviewTransferIntent(
                    targetPaperId,
                    pointer,
                    now);
            return;
        }

        if (EdgeCapsulePreviewPointerMovedBeyondTolerance(
                intent.Value.Anchor,
                pointer))
        {
            _edgeCapsulePreviewTransferIntent =
                new EdgeCapsulePreviewTransferIntent(
                    targetPaperId,
                    pointer,
                    now);
            return;
        }

        if (Stopwatch.GetElapsedTime(
                intent.Value.StableSinceTimestamp,
                now).TotalMilliseconds <
            EdgeCapsulePreviewTransferStableMilliseconds)
        {
            return;
        }

        _edgeCapsulePreviewTransferIntent = null;
        QueueEdgeCapsulePreviewTransfer(
            target,
            session.OwnerPaperId,
            intent.Value.Anchor);
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

        foreach (var paperId in session.QueuePaperIds)
        {
            if (!_windows.TryGetValue(paperId, out var window))
            {
                continue;
            }

            if (window.TryGetEdgeCapsuleAppliedBounds(out var bounds))
            {
                hasBounds = true;
                left = Math.Min(left, bounds.Left);
                top = Math.Min(top, bounds.Top);
                right = Math.Max(right, bounds.Right);
                bottom = Math.Max(bottom, bounds.Bottom);
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

        var corridorContains =
            pointer.X >= left - EdgeCapsulePreviewCorridorToleranceDevice &&
            pointer.X < right + EdgeCapsulePreviewCorridorToleranceDevice &&
            pointer.Y >= top - EdgeCapsulePreviewCorridorToleranceDevice &&
            pointer.Y < bottom + EdgeCapsulePreviewCorridorToleranceDevice;
        return new EdgeCapsulePreviewPointerResolution(
            target,
            corridorContains);
    }

    private static bool EdgeCapsulePreviewPointerMovedBeyondTolerance(
        DeviceScreenPoint anchor,
        DeviceScreenPoint pointer)
    {
        var deltaX = pointer.X - anchor.X;
        var deltaY = pointer.Y - anchor.Y;
        var tolerance = EdgeCapsulePreviewPointerToleranceDevice;
        return deltaX * deltaX + deltaY * deltaY >
            tolerance * tolerance;
    }
}
