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

    private readonly Dictionary<
        PaperWindow,
        EdgeCapsuleVisualTransactionEntry>
        _edgeCapsuleVisualTransactionEntries = new();
    private DispatcherOperation?
        _edgeCapsuleVisualTransactionCommitOperation;
    private readonly HashSet<string>
        _edgeCapsuleVisualTransactionQueueKeys =
            new(StringComparer.Ordinal);
    private readonly Dictionary<
        string,
        EdgeCapsuleQueueCompositionProxy>
        _edgeCapsuleQueueCompositionProxies =
            new(StringComparer.Ordinal);
    private readonly Dictionary<
        PaperWindow,
        EdgeCapsuleQueueCompositionProxy>
        _edgeCapsuleQueueCompositionProxyByWindow = new();
    private long _edgeCapsuleNativeTransactionGroupGeneration;

    internal void BeginEdgeCapsuleVisualTransaction(
        PaperWindow initiator)
    {
        if (IsExiting)
        {
            return;
        }

        var queueKey =
            QueueKey(initiator.EdgeCapsulePreviewPaper);
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Pending })
        {
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            return;
        }
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Executing })
        {
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            _edgeCapsuleVisualTransactionCommitOperation =
                initiator.Dispatcher.BeginInvoke(
                    (Action)CommitEdgeCapsuleVisualTransaction,
                    DispatcherPriority.Send);
            return;
        }

        _edgeCapsuleVisualTransactionQueueKeys.Clear();
        _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
        _edgeCapsuleVisualTransactionCommitOperation =
            initiator.Dispatcher.BeginInvoke(
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
            _edgeCapsuleVisualTransactionEntries[window] =
                existing with
                {
                    Motion =
                        MergeEdgeCapsuleVisualTransactionMotion(
                            existing.Motion,
                            motion),
                    RefreshLayout =
                        existing.RefreshLayout || refreshLayout
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

    private static EdgeCapsuleMotion
        MergeEdgeCapsuleVisualTransactionMotion(
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
#if DEBUG
        var commitStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var operation =
            _edgeCapsuleVisualTransactionCommitOperation;
        var transactionQueueKeys =
            _edgeCapsuleVisualTransactionQueueKeys
                .ToHashSet(StringComparer.Ordinal);
        var entries =
            _edgeCapsuleVisualTransactionEntries.Values.ToArray();
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
                .Where(entry =>
                    transactionQueueKeys.Contains(entry.QueueKey))
                .ToArray();
            if (EdgeCapsuleNativeTransactionPolicy
                    .RequiresCrossQueueGroup(
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
                        entry.Window
                            .JoinEdgeCapsuleNativeTransactionGroup(
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
                             !transactionQueueKeys.Contains(
                                 entry.QueueKey))
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
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"transaction.commit totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(commitStartedAt):F3} " +
                $"entries={entries.Length} queues={transactionQueueKeys.Count}");
#endif
            if (ReferenceEquals(
                    operation,
                    _edgeCapsuleVisualTransactionCommitOperation))
            {
                _edgeCapsuleVisualTransactionCommitOperation = null;
                _edgeCapsuleVisualTransactionQueueKeys.Clear();
            }
        }
    }

    private static readonly IReadOnlySet<string>
        EmptyQueueKeySet =
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

        var queueKeys = entries
            .Select(entry => entry.QueueKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        EdgeCapsuleQueueCompositionProxy? predecessor = null;
        var realHostMayHaveChanged = false;

        if (queueKeys.Length == 1)
        {
            _edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKeys[0],
                out predecessor);
            if (predecessor != null)
            {
                if (!predecessor.TryReserveForSuccessor())
                {
                    // A generation that is still starting or handing off already retains the real
                    // sources. Its completion resolves the latest reducer endpoint under cover.
                    return;
                }
                entries = CarryForwardEdgeCapsuleQueueProxyMembers(
                    entries,
                    predecessor);
            }
        }
        else
        {
            // Cross-queue visual ownership is deliberately not merged into one native target. End
            // each existing cover safely, then use the established batched HWND fallback.
            var completed = true;
            foreach (var queueKey in queueKeys)
            {
                if (_edgeCapsuleQueueCompositionProxies
                        .ContainsKey(queueKey))
                {
                    realHostMayHaveChanged = true;
                }
                completed &=
                    CompleteEdgeCapsuleQueueCompositionProxy(
                        queueKey,
                        success: true);
            }
            if (!completed)
            {
                return;
            }
        }

        var proxyPlan =
            TryCreateEdgeCapsuleQueueProxyPlan(
                entries,
                predecessor);
        if (proxyPlan != null)
        {
            var started =
                TryStartEdgeCapsuleQueueCompositionProxy(
                    proxyPlan,
                    entries,
                    predecessor,
                    out var proxyChangedRealHost);
            realHostMayHaveChanged |= proxyChangedRealHost;
            if (started)
            {
                if (CommitEdgeCapsuleQueueProxyLogicalEndpoints(
                        entries,
                        transactionTimestamp))
                {
                    return;
                }

                _ = CompleteEdgeCapsuleQueueCompositionProxy(
                    proxyPlan.QueueKey,
                    success: false);
                realHostMayHaveChanged = true;
            }

            // A failed published generation may retain its cover for endpoint/uncloak retry. Never
            // drive a native fallback through that queue while any compositor owner remains.
            if (_edgeCapsuleQueueCompositionProxies.TryGetValue(
                    proxyPlan.QueueKey,
                    out var retained) &&
                !ReferenceEquals(retained, predecessor))
            {
                return;
            }
        }

        if (predecessor != null &&
            _edgeCapsuleQueueCompositionProxies.TryGetValue(
                predecessor.QueueKey,
                out var stillCurrent) &&
            ReferenceEquals(stillCurrent, predecessor))
        {
            predecessor.CompleteAfterFailedSuccessor(
                success: true);
            return;
        }

        if (predecessor != null)
        {
            // If predecessor finished while successor admission failed, its final handoff already
            // applied the latest reducer endpoint. Native fallback must snap from that authority.
            realHostMayHaveChanged = true;
        }

        if (realHostMayHaveChanged)
        {
            entries = entries
                .Select(entry => entry with
                {
                    Motion = EdgeCapsuleMotion.Snap(
                        entry.Motion.Reason)
                })
                .ToArray();
        }

        CommitEdgeCapsuleVisualTransactionNativeFallback(
            entries,
            transactionQueueKeys,
            transactionTimestamp);
    }

    private static EdgeCapsuleVisualTransactionEntry[]
        CarryForwardEdgeCapsuleQueueProxyMembers(
            EdgeCapsuleVisualTransactionEntry[] entries,
            EdgeCapsuleQueueCompositionProxy predecessor)
    {
        var durationMilliseconds = entries
            .Where(entry =>
                entry.Motion.Kind == EdgeCapsuleMotionKind.Animate)
            .Select(entry => entry.Motion.DurationMilliseconds)
            .DefaultIfEmpty(EdgeCapsuleLayout.SlotMoveMilliseconds)
            .Max();
        var byWindow = entries.ToDictionary(
            entry => entry.Window);

        foreach (var member in predecessor.Members)
        {
            if (member.Window.IsClosed)
            {
                continue;
            }
            if (byWindow.TryGetValue(
                    member.Window,
                    out var existing))
            {
                if (existing.Motion.Kind !=
                    EdgeCapsuleMotionKind.Animate)
                {
                    byWindow[member.Window] = existing with
                    {
                        Motion = EdgeCapsuleMotion.Animate(
                            EdgeCapsuleTransitionReason.Placement,
                            durationMilliseconds)
                    };
                }
                continue;
            }

            byWindow[member.Window] =
                new EdgeCapsuleVisualTransactionEntry(
                    member.Window,
                    predecessor.QueueKey,
                    EdgeCapsuleMotion.Animate(
                        EdgeCapsuleTransitionReason.Placement,
                        durationMilliseconds),
                    RefreshLayout: false);
        }
        return byWindow.Values.ToArray();
    }

    private void CommitEdgeCapsuleVisualTransactionNativeFallback(
        EdgeCapsuleVisualTransactionEntry[] entries,
        IReadOnlySet<string> transactionQueueKeys,
        long transactionTimestamp)
    {
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
                WindowNative.BeginWindowDeviceBoundsBatch(
                    entries.Length);
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
                    motion = EdgeCapsuleMotion.Snap(
                        motion.Reason);
                }
                else if (!belongsToTransactionQueue &&
                    motion.Kind == EdgeCapsuleMotionKind.Snap)
                {
                    motion = EdgeCapsuleMotion.Preserve(
                        motion.Reason);
                }

                var status =
                    entry.Window.CommitEdgeCapsuleVisualTransaction(
                        motion,
                        entry.RefreshLayout,
                        transactionTimestamp,
                        rebaseActiveTransition:
                            belongsToTransactionQueue);
                if (status ==
                    EdgeCapsuleNativeBatchApplyStatus.Deferred)
                {
                    logicalBatchDeferred = true;
                }
                else if (status ==
                    EdgeCapsuleNativeBatchApplyStatus.Failed)
                {
                    logicalBatchFailed = true;
                }
            }

            nativeBatchCommitted = nativeBoundsBatch.Commit();
            transactionDeferred =
                nativeBatchCommitted &&
                logicalBatchDeferred &&
                !logicalBatchFailed;
            transactionCommitted =
                nativeBatchCommitted &&
                !logicalBatchDeferred &&
                !logicalBatchFailed;

            foreach (var entry in entries)
            {
                entry.Window
                    .CompleteEdgeCapsuleVisualTransactionApply(
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
                entry.Window
                    .PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
    }

    private EdgeCapsuleQueueProxyPlan?
        TryCreateEdgeCapsuleQueueProxyPlan(
            EdgeCapsuleVisualTransactionEntry[] entries,
            EdgeCapsuleQueueCompositionProxy? predecessor = null)
    {
        if (entries.Length == 0 ||
            entries.Select(entry => entry.QueueKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            return null;
        }

        var queueKey = entries[0].QueueKey;
        if (!AllowsEdgeCapsuleQueueProxyOwnership(queueKey))
        {
            return null;
        }

        var candidates =
            new List<EdgeCapsuleQueueProxyCandidate>(
                entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Window.IsClosed)
            {
                return null;
            }

            EdgeCapsulePresentationFrame? start = null;
            EdgeCapsulePresentationFrame? source = null;
            var retained = false;
            if (predecessor != null &&
                predecessor.TryGetPresentation(
                    entry.Window,
                    out var sampled) &&
                predecessor.TryGetSourcePresentation(
                    entry.Window,
                    out var nativeSource))
            {
                start = sampled;
                source = nativeSource;
                retained = predecessor.RetainsSource(
                    entry.Window);
            }

            var candidate = entry.Window
                .CaptureEdgeCapsuleQueueProxyCandidate(
                    entry.QueueKey,
                    entry.Motion,
                    start,
                    source,
                    retained);
            if (!candidate.HasValue)
            {
                return null;
            }
            candidates.Add(candidate.Value);
        }

        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
            queueKey,
            candidates);
        if (plan == null)
        {
            return null;
        }

        // Size the persistent queue output for every member's possible native preview, not only the
        // current owner. This keeps rapid A -> B successor roots on the same HWND even when B is a
        // wider/taller plugin, while still limiting the transparent output to queue-owned geometry.
        var capacityEnvelope = plan.Envelope;
        var maximumDownwardShift = 0;
        var workAreaBottom = plan.Envelope.Bottom;
        foreach (var capacityWindow in _windows.Values.Where(window =>
                     !window.IsClosed &&
                     string.Equals(
                         QueueKey(window.EdgeCapsulePreviewPaper),
                         queueKey,
                         StringComparison.Ordinal)))
        {
            var capacity = capacityWindow
                .CaptureEdgeCapsuleQueueProxyCapacity();
            if (capacity.PreviewBounds.IsEmpty)
            {
                continue;
            }
            capacityEnvelope = EdgeCapsuleQueueProxyGeometry.Union(
                capacityEnvelope,
                capacity.PreviewBounds);
            maximumDownwardShift = Math.Max(
                maximumDownwardShift,
                capacity.MaximumDownwardShiftDevice);
            workAreaBottom = Math.Max(
                workAreaBottom,
                capacity.WorkAreaBottomDevice);
        }
        capacityEnvelope =
            EdgeCapsuleQueueProxyGeometry.WithDownwardCapacity(
                capacityEnvelope,
                maximumDownwardShift,
                workAreaBottom);
        return plan with
        {
            Envelope = capacityEnvelope
        };
    }

    private static bool
        CommitEdgeCapsuleQueueProxyLogicalEndpoints(
            EdgeCapsuleVisualTransactionEntry[] entries,
            long transactionTimestamp)
    {
        var committed = true;
        using (entries[0].Window.Dispatcher.DisableProcessing())
        {
            foreach (var entry in entries)
            {
                if (entry.Window.IsClosed)
                {
                    continue;
                }

                var status =
                    entry.Window.CommitEdgeCapsuleVisualTransaction(
                        EdgeCapsuleMotion.Snap(
                            entry.Motion.Reason),
                        entry.RefreshLayout,
                        transactionTimestamp,
                        rebaseActiveTransition: true);
                committed &= status ==
                    EdgeCapsuleNativeBatchApplyStatus.Ready;
            }

            foreach (var entry in entries)
            {
                entry.Window
                    .CompleteEdgeCapsuleVisualTransactionApply(
                        success: committed,
                        deferred: false,
                        transactionTimestamp);
            }
        }

        if (!committed)
        {
            return false;
        }
        foreach (var entry in entries)
        {
            if (!entry.Window.IsClosed)
            {
                entry.Window
                    .PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
        return true;
    }

    private bool TryStartEdgeCapsuleQueueCompositionProxy(
        EdgeCapsuleQueueProxyPlan plan,
        EdgeCapsuleVisualTransactionEntry[] entries,
        EdgeCapsuleQueueCompositionProxy? predecessor,
        out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        var sessionOrdinal =
            EdgeCapsuleQueueCompositionProxy
                .ReserveSessionOrdinal();
        var byPaperId = entries
            .GroupBy(entry =>
                entry.Window.EdgeCapsulePreviewPaperId,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.Ordinal);
        var members =
            new List<EdgeCapsuleQueueCompositionProxyMember>(
                plan.Members.Count);
        var preparedSnapshots =
            new Dictionary<string, (
                EdgeCapsulePresentationFrame Source,
                EdgeCapsuleProxySnapshotHost Host)>(
                StringComparer.Ordinal);
        EdgeCapsuleQueueCompositionProxy? proxy = null;
        try
        {
            // Prepare a complete 1:1 copy of the real source while the predecessor compositor keeps
            // advancing. The final successor start is latched afterwards and represented by a
            // rounded clip into this full source; snapshot-host layout can therefore never make the
            // sampled A-to-B frame stale.
            foreach (var memberPlan in plan.Members)
            {
                if (!byPaperId.TryGetValue(
                        memberPlan.PaperId,
                        out var entry))
                {
                    return false;
                }

                EdgeCapsuleProxySnapshotHost? snapshotHost = null;
                if (memberPlan.RequiresStartSnapshot)
                {
                    var prefetched =
                        TryTakeEdgeCapsulePreviewPreparedSnapshot(
                            memberPlan.PaperId,
                            memberPlan.Source,
                            out var preparedSnapshotHost);
#if DEBUG
                    var captureMilliseconds = 0.0;
                    var hostMilliseconds = 0.0;
#endif
                    if (prefetched)
                    {
                        snapshotHost = preparedSnapshotHost;
                    }
                    else
                    {
#if DEBUG
                        var captureStartedAt =
                            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                        var snapshot = entry.Window
                            .CaptureEdgeCapsuleQueueProxySnapshot(
                                memberPlan.Source);
#if DEBUG
                        captureMilliseconds =
                            EdgeCapsulePerformanceDiagnostics
                                .ElapsedMilliseconds(
                                    captureStartedAt);
                        var hostStartedAt =
                            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                        snapshotHost = snapshot == null
                            ? null
                            : EdgeCapsuleProxySnapshotHost.TryCreate(
                                snapshot,
                                memberPlan.Source);
#if DEBUG
                        hostMilliseconds =
                            EdgeCapsulePerformanceDiagnostics
                                .ElapsedMilliseconds(
                                    hostStartedAt);
#endif
                    }
#if DEBUG
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"proxy.snapshot session={sessionOrdinal} " +
                        $"cold={sessionOrdinal == 1} queue={plan.QueueKey} " +
                        $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(memberPlan.PaperId)} " +
                        $"prefetched={prefetched} " +
                        $"captureMs={captureMilliseconds:F3} " +
                        $"hostCreateMs={hostMilliseconds:F3} " +
                        $"outcome={(snapshotHost == null ? "failed" : "ready")} " +
                        $"pixels={(long)memberPlan.Source.Bounds.Width * memberPlan.Source.Bounds.Height}");
#endif
                    if (snapshotHost == null)
                    {
                        return false;
                    }
                    preparedSnapshots.Add(
                        memberPlan.PaperId,
                        (memberPlan.Source, snapshotHost));
                }
            }

            if (predecessor != null)
            {
                if (!predecessor.TryLatchForSuccessor())
                {
                    return false;
                }

                var latchedPlan =
                    TryCreateEdgeCapsuleQueueProxyPlan(
                        entries,
                        predecessor);
                if (latchedPlan == null ||
                    !string.Equals(
                        latchedPlan.QueueKey,
                        plan.QueueKey,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                plan = latchedPlan;
            }

            foreach (var memberPlan in plan.Members)
            {
                if (!byPaperId.TryGetValue(
                        memberPlan.PaperId,
                        out var entry))
                {
                    return false;
                }

                EdgeCapsuleProxySnapshotHost? snapshotHost = null;
                if (memberPlan.RequiresStartSnapshot)
                {
                    if (!preparedSnapshots.TryGetValue(
                            memberPlan.PaperId,
                            out var prepared) ||
                        prepared.Source != memberPlan.Source)
                    {
                        return false;
                    }
                    preparedSnapshots.Remove(memberPlan.PaperId);
                    snapshotHost = prepared.Host;
                }

                members.Add(
                    new EdgeCapsuleQueueCompositionProxyMember(
                        entry.Window,
                        memberPlan,
                        entry.Window
                            .EdgeCapsuleQueueProxySourceHandle,
                        snapshotHost));
            }

            proxy = EdgeCapsuleQueueCompositionProxy.TryCreate(
                sessionOrdinal,
                plan,
                members,
                predecessor,
                interactionRequested: (point, message) =>
                    CompleteAndRouteEdgeCapsuleQueueProxyInput(
                        plan.QueueKey,
                        point,
                        message),
                environmentChanged: () =>
                    CompleteEdgeCapsuleQueueCompositionProxy(
                        plan.QueueKey,
                        success: false),
                coverReady: successor =>
                    PublishEdgeCapsuleQueueCompositionProxy(
                        plan.QueueKey,
                        successor,
                        predecessor),
                completed: (completedProxy, success) =>
                    FinishEdgeCapsuleQueueCompositionProxy(
                        plan.QueueKey,
                        completedProxy,
                        success));
            if (proxy == null)
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"proxy.session phase=fallback session={sessionOrdinal} " +
                    $"cold={sessionOrdinal == 1} queue={plan.QueueKey} reason=create-failed");
#endif
                return false;
            }

            if (!proxy.TryStart(out realHostMayHaveChanged))
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"proxy.session phase=fallback session={sessionOrdinal} " +
                    $"cold={sessionOrdinal == 1} queue={plan.QueueKey} reason=startup-failed " +
                    $"published={proxy.CoverPublished}");
#endif
                if (proxy.CoverPublished)
                {
                    _ = FinishEdgeCapsuleQueueCompositionProxy(
                        plan.QueueKey,
                        proxy,
                        success: false);
                    realHostMayHaveChanged = true;
                }
                else
                {
                    proxy.AbortStaged();
                }
                return false;
            }
            return true;
        }
        finally
        {
            if (proxy == null)
            {
                foreach (var member in members)
                {
                    member.SnapshotHost?.Dispose();
                }
            }
            foreach (var prepared in preparedSnapshots.Values)
            {
                prepared.Host.Dispose();
            }
        }
    }

    private bool PublishEdgeCapsuleQueueCompositionProxy(
        string queueKey,
        EdgeCapsuleQueueCompositionProxy successor,
        EdgeCapsuleQueueCompositionProxy? predecessor)
    {
        if (predecessor == null)
        {
            if (_edgeCapsuleQueueCompositionProxies
                    .ContainsKey(queueKey))
            {
                return false;
            }
        }
        else
        {
            if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(
                    queueKey,
                    out var current) ||
                !ReferenceEquals(current, predecessor) ||
                !predecessor.TryTransferCloakedSourcesTo(
                    successor))
            {
                return false;
            }
        }

        if (predecessor != null)
        {
            foreach (var pair in
                     _edgeCapsuleQueueCompositionProxyByWindow
                         .Where(pair =>
                             ReferenceEquals(
                                 pair.Value,
                                 predecessor))
                         .ToArray())
            {
                _edgeCapsuleQueueCompositionProxyByWindow
                    .Remove(pair.Key);
            }
        }

        _edgeCapsuleQueueCompositionProxies[queueKey] =
            successor;
        foreach (var member in successor.Members)
        {
            _edgeCapsuleQueueCompositionProxyByWindow[
                member.Window] = successor;
        }

        if (predecessor != null)
        {
            predecessor.DisposeAfterSuccessorTransfer();
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.successor phase=promote from={predecessor.SessionOrdinal} " +
                $"to={successor.SessionOrdinal} queue={queueKey} " +
                $"members={successor.Members.Count}");
#endif
        }
        return true;
    }

    private bool FinishEdgeCapsuleQueueCompositionProxy(
        string queueKey,
        EdgeCapsuleQueueCompositionProxy? expected,
        bool success)
    {
        if (expected == null ||
            !_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var current) ||
            !ReferenceEquals(expected, current))
        {
            return true;
        }

        var windows = current.Members
            .Select(member => member.Window)
            .Distinct()
            .ToArray();

        if (current.CoverLost)
        {
            var sourcesReleased =
                current.ReleaseAfterCoverLoss();
            if (!sourcesReleased)
            {
                current.ScheduleCompletionRetry(
                    success: false);
                return false;
            }

            _edgeCapsuleQueueCompositionProxies
                .Remove(queueKey);
            foreach (var window in windows)
            {
                if (_edgeCapsuleQueueCompositionProxyByWindow
                        .TryGetValue(window, out var routed) &&
                    ReferenceEquals(routed, current))
                {
                    _edgeCapsuleQueueCompositionProxyByWindow
                        .Remove(window);
                }
            }
            try
            {
                current.Dispose();
            }
            catch
            {
                current.ForceDisposeForShutdown();
            }

            foreach (var window in windows)
            {
                if (!window.IsClosed)
                {
                    try
                    {
                        window.FlushEdgeCapsuleQueueProxyEndpoint();
                    }
                    catch { }
                }
                try
                {
                    window
                        .ReleaseDeferredEdgeCapsuleQueueProxyPreviewContent();
                }
                catch { }
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=emergency-release session={current.SessionOrdinal} " +
                $"cold={current.IsColdSession} queue={queueKey} members={windows.Length}");
#endif
            return true;
        }

        var endpointsReady = true;
        var endpoints = new List<(
            PaperWindow Window,
            EdgeCapsulePresentationFrame Endpoint)>(windows.Length);
#if DEBUG
        var handoffStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        var endpointStartedAt = handoffStartedAt;
#endif
        try
        {
            foreach (var window in windows)
            {
                var applied = window
                    .TryApplyLatestEdgeCapsuleQueueProxyEndpoint(
                        out var endpoint);
                endpointsReady &= applied;
                endpoints.Add((window, endpoint));
            }

            if (endpointsReady)
            {
                foreach (var item in endpoints.Where(item =>
                             item.Endpoint.Visible))
                {
                    endpointsReady &= item.Window
                        .PrepareEdgeCapsuleQueueProxyEndpointLayoutForHandoff();
                }
            }

            if (endpointsReady &&
                endpoints.Any(item => item.Endpoint.Visible))
            {
                // Submit every real endpoint in one WPF render turn, but do not cross DWM here.
                // TryReleaseForHandoff publishes these queued surface updates together with the
                // real/proxy authority swap. A separate flush held the final proxy frame for one
                // more presentation and made close look frozen before its last-frame flash.
                windows[0].Dispatcher.Invoke(
                    static () => { },
                    DispatcherPriority.Render);
            }

            if (endpointsReady)
            {
                foreach (var item in endpoints)
                {
                    endpointsReady &= item.Window
                        .VerifyEdgeCapsuleQueueProxyEndpoint(
                            item.Endpoint);
                }
            }
        }
        catch (Exception ex)
        {
            endpointsReady = false;
            Trace.TraceError(
                "Edge capsule queue proxy handoff failed. Queue={0}; Session={1}; Exception={2}",
                queueKey,
                current.SessionOrdinal,
                ex);
        }

        if (!endpointsReady)
        {
            current.ScheduleCompletionRetry(
                success: false);
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=handoff session={current.SessionOrdinal} " +
                $"cold={current.IsColdSession} queue={queueKey} " +
                $"members={windows.Length} requestedSuccess={success} " +
                $"ready=false retry=true totalMs=" +
                $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointStartedAt):F3}");
#endif
            return false;
        }

        if (!current.TryReleaseForHandoff())
        {
            current.ScheduleCompletionRetry(
                success: false);
            return false;
        }

        _edgeCapsuleQueueCompositionProxies.Remove(queueKey);
        foreach (var window in windows)
        {
            if (_edgeCapsuleQueueCompositionProxyByWindow
                    .TryGetValue(window, out var routed) &&
                ReferenceEquals(routed, current))
            {
                _edgeCapsuleQueueCompositionProxyByWindow
                    .Remove(window);
            }
        }
        try
        {
            current.Dispose();
        }
        catch
        {
            current.ForceDisposeForShutdown();
        }

        foreach (var window in windows)
        {
            try
            {
                window
                    .ReleaseDeferredEdgeCapsuleQueueProxyPreviewContent();
            }
            catch { }
            if (!window.IsClosed)
            {
                try
                {
                    window.FlushEdgeCapsuleQueueProxyEndpoint();
                }
                catch { }
            }
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.session phase=complete session={current.SessionOrdinal} " +
            $"cold={current.IsColdSession} queue={queueKey} " +
            $"members={windows.Length} requestedSuccess={success} " +
            $"endpointsReady={endpointsReady} handoffMs=" +
            $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(handoffStartedAt):F3}");
#endif
        return true;
    }

    private bool CompleteEdgeCapsuleQueueCompositionProxy(
        string queueKey,
        bool success)
    {
        if (_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var proxy))
        {
            proxy.CompleteNow(success);
        }
        return !_edgeCapsuleQueueCompositionProxies
            .ContainsKey(queueKey);
    }

    private void CompleteAndRouteEdgeCapsuleQueueProxyInput(
        string queueKey,
        DeviceScreenPoint point,
        int message)
    {
        if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var proxy))
        {
            return;
        }

        var hasTarget = proxy.TryResolveInputTarget(
            point,
            out var targetHandle,
            out var endpointPoint);
        proxy.CompleteNow(success: true);
        var handoffCompleted =
            !_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var remaining) ||
            !ReferenceEquals(remaining, proxy);
        if (hasTarget && handoffCompleted)
        {
            _ = WindowNative.TryPostMouseButtonDown(
                targetHandle,
                message,
                endpointPoint);
        }
    }

    internal bool TryRouteEdgeCapsuleQueueProxyApply(
        PaperWindow window,
        EdgeCapsulePresentationFrame frame,
        out bool applied)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy) &&
            proxy.Routes(window) &&
            proxy.TryRouteApply(window, frame))
        {
            applied = true;
            return true;
        }
        applied = false;
        return false;
    }

    internal bool TryGetEdgeCapsuleQueueProxyPresentation(
        PaperWindow window,
        out EdgeCapsulePresentationFrame frame)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy))
        {
            return proxy.TryGetPresentation(
                window,
                out frame);
        }
        frame = EdgeCapsulePresentationFrame.Hidden;
        return false;
    }

    internal bool TryGetEdgeCapsuleQueueProxyDiagnosticHandle(
        PaperWindow window,
        out IntPtr handle)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy))
        {
            handle = proxy.OutputHandle;
            return handle != IntPtr.Zero;
        }
        handle = IntPtr.Zero;
        return false;
    }

    internal bool IsEdgeCapsuleQueueProxyRetainingSource(
        PaperWindow window) =>
        _edgeCapsuleQueueCompositionProxyByWindow
            .TryGetValue(window, out var proxy) &&
        proxy.RetainsSource(window);

    internal void CompleteEdgeCapsuleQueueCompositionProxyFor(
        PaperWindow window,
        bool success = false)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy))
        {
            proxy.CompleteNow(success);
        }
    }

    private void DisposeEdgeCapsuleQueueCompositionProxies()
    {
        var proxies =
            _edgeCapsuleQueueCompositionProxies.Values
                .Distinct()
                .ToArray();
        _edgeCapsuleQueueCompositionProxies.Clear();
        _edgeCapsuleQueueCompositionProxyByWindow.Clear();
        foreach (var proxy in proxies)
        {
            proxy.ForceDisposeForShutdown();
        }
    }
}
