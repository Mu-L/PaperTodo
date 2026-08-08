using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const int EdgeCapsulePreviewCorridorToleranceDevice = 10;

    private EdgeCapsulePreviewLayoutSession? _edgeCapsulePreviewSession;
    private string? _edgeCapsulePreviewOutgoingPaperId;
    private DeviceScreenPoint? _edgeCapsulePreviewLastTransferPointer;
    private int _edgeCapsulePreviewTransferGeneration;
    private int _edgeCapsulePreviewCloseGeneration;

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
            if (IsEdgeCapsulePreviewLayoutOnlyEnter())
            {
                return;
            }

            QueueEdgeCapsulePreviewTransfer(window);
            return;
        }

        // Use the next input turn only to avoid re-entering pointer reconciliation. There is no
        // hover dwell: the shell transition starts as soon as that zero-delay callback runs.
        QueueEdgeCapsulePreviewTransfer(window);
    }

    internal void NotifyEdgeCapsulePreviewPointerSample(
        PaperWindow window,
        DeviceScreenPoint? pointer)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null ||
            !string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal) ||
            !pointer.HasValue)
        {
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer.Value);
        if (!_edgeCapsulePreviewLastTransferPointer.HasValue &&
            TryTransferEdgeCapsulePreviewToHoveredPeer(session))
        {
            return;
        }
        if (window.EdgeCapsulePreviewInteractionActive ||
            EdgeCapsulePreviewCorridorContains(session, pointer.Value))
        {
            _edgeCapsulePreviewCloseGeneration++;
            return;
        }

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
        var generation = ++_edgeCapsulePreviewCloseGeneration;
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (IsExiting ||
                    generation != _edgeCapsulePreviewCloseGeneration ||
                    _edgeCapsulePreviewSession is not { } session ||
                    !string.Equals(
                        session.OwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(ownerPaperId, out var owner) ||
                    !ReferenceEquals(owner, window) ||
                    owner.EdgeCapsulePreviewInteractionActive ||
                    !WindowNative.TryGetCursorScreenPosition(out var pointer) ||
                    EdgeCapsulePreviewCorridorContains(session, pointer))
                {
                    return;
                }

                if (TryTransferEdgeCapsulePreviewToHoveredPeer(
                        session,
                        sameQueueOnly: false))
                {
                    return;
                }

                CloseEdgeCapsulePreview(animate: true, arrange: true);
            }),
            DispatcherPriority.Input);
    }

    private void QueueEdgeCapsulePreviewTransfer(PaperWindow window)
    {
        var paperId = window.EdgeCapsulePreviewPaperId;
        var generation = ++_edgeCapsulePreviewTransferGeneration;
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (IsExiting ||
                    generation != _edgeCapsulePreviewTransferGeneration ||
                    !_windows.TryGetValue(paperId, out var current) ||
                    !ReferenceEquals(current, window) ||
                    !current.CanEnterEdgeCapsulePreview ||
                    !current.IsEdgeCapsulePointerOver)
                {
                    return;
                }

                var session = _edgeCapsulePreviewSession;
                if (session != null &&
                    string.Equals(
                        session.OwnerPaperId,
                        paperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                OpenOrTransferEdgeCapsulePreview(current);
            }),
            DispatcherPriority.Input);
    }

    private void OpenOrTransferEdgeCapsulePreview(PaperWindow window)
    {
        if (IsExiting ||
            !window.CanEnterEdgeCapsulePreview ||
            !window.IsEdgeCapsulePointerOver)
        {
            return;
        }

        _edgeCapsulePreviewTransferGeneration++;
        _edgeCapsulePreviewCloseGeneration++;
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

    private bool IsEdgeCapsulePreviewLayoutOnlyEnter()
    {
        if (!_edgeCapsulePreviewLastTransferPointer.HasValue ||
            !WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            return false;
        }

        var anchor = _edgeCapsulePreviewLastTransferPointer.Value;
        if (Math.Abs(pointer.X - anchor.X) <= 1 &&
            Math.Abs(pointer.Y - anchor.Y) <= 1)
        {
            return true;
        }

        _edgeCapsulePreviewLastTransferPointer = null;
        return false;
    }

    private void ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
        DeviceScreenPoint pointer)
    {
        if (!_edgeCapsulePreviewLastTransferPointer.HasValue)
        {
            return;
        }

        var anchor = _edgeCapsulePreviewLastTransferPointer.Value;
        if (Math.Abs(pointer.X - anchor.X) > 1 ||
            Math.Abs(pointer.Y - anchor.Y) > 1)
        {
            _edgeCapsulePreviewLastTransferPointer = null;
        }
    }

    private bool TryTransferEdgeCapsulePreviewToHoveredPeer(
        EdgeCapsulePreviewLayoutSession session,
        bool sameQueueOnly = true)
    {
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (string.Equals(
                    paper.Id,
                    session.OwnerPaperId,
                    StringComparison.Ordinal) ||
                (sameQueueOnly &&
                 !string.Equals(
                     QueueKey(paper),
                     session.QueueKey,
                     StringComparison.Ordinal)) ||
                !_windows.TryGetValue(paper.Id, out var peer) ||
                !peer.CanEnterEdgeCapsulePreview ||
                !peer.IsEdgeCapsulePointerOver)
            {
                continue;
            }

            QueueEdgeCapsulePreviewTransfer(peer);
            return true;
        }

        return false;
    }

    private bool EdgeCapsulePreviewCorridorContains(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        var hasBounds = false;
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (!string.Equals(
                    QueueKey(paper),
                    session.QueueKey,
                    StringComparison.Ordinal) ||
                !_windows.TryGetValue(paper.Id, out var window) ||
                !window.TryGetEdgeCapsuleAppliedBounds(out var bounds))
            {
                continue;
            }

            hasBounds = true;
            left = Math.Min(left, bounds.Left);
            top = Math.Min(top, bounds.Top);
            right = Math.Max(right, bounds.Right);
            bottom = Math.Max(bottom, bounds.Bottom);
        }

        if (!hasBounds)
        {
            return false;
        }

        return pointer.X >= left - EdgeCapsulePreviewCorridorToleranceDevice &&
            pointer.X < right + EdgeCapsulePreviewCorridorToleranceDevice &&
            pointer.Y >= top - EdgeCapsulePreviewCorridorToleranceDevice &&
            pointer.Y < bottom + EdgeCapsulePreviewCorridorToleranceDevice;
    }
}
