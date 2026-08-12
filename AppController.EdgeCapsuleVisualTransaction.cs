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

    private readonly Dictionary<PaperWindow, EdgeCapsuleVisualTransactionEntry>
        _edgeCapsuleVisualTransactionEntries = new();
    private DispatcherOperation? _edgeCapsuleVisualTransactionCommitOperation;
    private readonly HashSet<string> _edgeCapsuleVisualTransactionQueueKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, EdgeCapsuleQueueCompositionProxy>
        _edgeCapsuleQueueCompositionProxies = new(StringComparer.Ordinal);
    private readonly Dictionary<PaperWindow, EdgeCapsuleQueueCompositionProxy>
        _edgeCapsuleQueueCompositionProxyByWindow = new();
    private long _edgeCapsuleNativeTransactionGroupGeneration;

    internal void BeginEdgeCapsuleVisualTransaction(PaperWindow initiator)
    {
        if (IsExiting)
        {
            return;
        }

        var queueKey = QueueKey(initiator.EdgeCapsulePreviewPaper);
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Pending })
        {
            // Cross-queue preview transfer calls Begin once for the new owner and once for the old
            // owner. Both logical queues belong to the same atomic visual transaction.
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            return;
        }
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Executing })
        {
            // Re-entrant model/content work must never be stranded behind the transaction that
            // triggered it. Queue a fresh Send pass after this callback returns.
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            _edgeCapsuleVisualTransactionCommitOperation = initiator.Dispatcher.BeginInvoke(
                (Action)CommitEdgeCapsuleVisualTransaction,
                DispatcherPriority.Send);
            return;
        }

        _edgeCapsuleVisualTransactionQueueKeys.Clear();
        _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
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
                    QueueKey(window.EdgeCapsulePreviewPaper),
                    motion,
                    refreshLayout);
        }
        return true;
    }

    private static EdgeCapsuleMotion MergeEdgeCapsuleVisualTransactionMotion(
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
        var commitStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var operation = _edgeCapsuleVisualTransactionCommitOperation;
        var transactionQueueKeys = _edgeCapsuleVisualTransactionQueueKeys
            .ToHashSet(StringComparer.Ordinal);
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
            var transactionEntries = entries
                .Where(entry => transactionQueueKeys.Contains(entry.QueueKey))
                .ToArray();
            if (EdgeCapsuleNativeTransactionPolicy.RequiresCrossQueueGroup(
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
                        entry.Window.JoinEdgeCapsuleNativeTransactionGroup(
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
                             !transactionQueueKeys.Contains(entry.QueueKey))
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

    private static readonly IReadOnlySet<string> EmptyQueueKeySet =
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

        // A rapid A→B→C browse first reveals the already committed endpoint. This avoids stacking
        // three live preview sources and gives the next transaction one authoritative start frame.
        var previousProxiesCompleted = true;
        foreach (var queueKey in entries
                     .Select(entry => entry.QueueKey)
                     .Distinct(StringComparer.Ordinal))
        {
            previousProxiesCompleted &= CompleteEdgeCapsuleQueueCompositionProxy(
                queueKey,
                success: true);
        }
        if (!previousProxiesCompleted)
        {
            // The retained cover will replay the latest reducer/layout endpoint on its retry.
            // Never overwrite its queue routing with a second live session.
            return;
        }

        var proxyPlan = TryCreateEdgeCapsuleQueueProxyPlan(entries);
        var realHostMayHaveChanged = false;
        if (proxyPlan != null &&
            TryStartEdgeCapsuleQueueCompositionProxy(
                proxyPlan,
                entries,
                out realHostMayHaveChanged))
        {
            // The real HWNDs and the reducer both move to the endpoint immediately. Commit the
            // Presenters to that same endpoint while proxy routing is active: this advances commit
            // versions (so deferred preview content can render), publishes one coherent queue
            // notification, and leaves DirectComposition as the only animation clock.
            if (CommitEdgeCapsuleQueueProxyLogicalEndpoints(
                    entries,
                    transactionTimestamp))
            {
                return;
            }

            CompleteEdgeCapsuleQueueCompositionProxy(
                proxyPlan.QueueKey,
                success: false);
            realHostMayHaveChanged = true;
        }
        if (realHostMayHaveChanged)
        {
            // A compositor/endpoint failure after cloaking must never replay the old geometry on
            // the newly laid out real host. Snap the authoritative target through the normal batch.
            entries = entries
                .Select(entry => entry with
                {
                    Motion = EdgeCapsuleMotion.Snap(entry.Motion.Reason)
                })
                .ToArray();
        }

#if DEBUG
        var groupStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        double entryMilliseconds = 0;
        double nativeCommitMilliseconds = 0;
        double completionMilliseconds = 0;
        double notificationMilliseconds = 0;
        double slowestEntryMilliseconds = 0;
        var slowestEntry = "<none>";
#endif

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
                WindowNative.BeginWindowDeviceBoundsBatch(entries.Length);
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
                    motion = EdgeCapsuleMotion.Snap(motion.Reason);
                }
                else if (!belongsToTransactionQueue &&
                    motion.Kind == EdgeCapsuleMotionKind.Snap)
                {
                    // Global arrange can stage an unrelated queue. Preserve its in-flight target,
                    // but commit it in a separate native batch so another queue cannot poison it.
                    motion = EdgeCapsuleMotion.Preserve(motion.Reason);
                }

#if DEBUG
                var entryStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                var applyStatus = entry.Window.CommitEdgeCapsuleVisualTransaction(
                    motion,
                    entry.RefreshLayout,
                    transactionTimestamp,
                    rebaseActiveTransition: belongsToTransactionQueue);
#if DEBUG
                var currentEntryMilliseconds =
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        entryStartedAt);
                entryMilliseconds += currentEntryMilliseconds;
                if (currentEntryMilliseconds > slowestEntryMilliseconds)
                {
                    slowestEntryMilliseconds = currentEntryMilliseconds;
                    slowestEntry = EdgeCapsulePerformanceDiagnostics.ShortId(
                        entry.Window.EdgeCapsulePreviewPaperId);
                }
#endif
                if (applyStatus == EdgeCapsuleNativeBatchApplyStatus.Deferred)
                {
                    logicalBatchDeferred = true;
                }
                else if (applyStatus == EdgeCapsuleNativeBatchApplyStatus.Failed)
                {
                    logicalBatchFailed = true;
                }
            }

#if DEBUG
            var nativeCommitStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            nativeBatchCommitted = nativeBoundsBatch.Commit();
#if DEBUG
            nativeCommitMilliseconds +=
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    nativeCommitStartedAt);
#endif
            transactionDeferred = nativeBatchCommitted &&
                logicalBatchDeferred &&
                !logicalBatchFailed;
            transactionCommitted = nativeBatchCommitted &&
                !logicalBatchDeferred &&
                !logicalBatchFailed;
#if DEBUG
            var completionStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            foreach (var entry in entries)
            {
                entry.Window.CompleteEdgeCapsuleVisualTransactionApply(
                    transactionCommitted,
                    transactionDeferred,
                    transactionTimestamp);
            }
#if DEBUG
            completionMilliseconds +=
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    completionStartedAt);
#endif
        }

        if (!transactionCommitted)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"transaction.group outcome={(transactionDeferred ? "deferred" : "failed")} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(groupStartedAt):F3} " +
                $"entriesMs={entryMilliseconds:F3} nativeCommitMs={nativeCommitMilliseconds:F3} " +
                $"completeMs={completionMilliseconds:F3} entries={entries.Length} " +
                $"slowest={slowestEntry}:{slowestEntryMilliseconds:F3}");
#endif
            return;
        }

#if DEBUG
        var notificationStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        foreach (var entry in entries)
        {
            if (!entry.Window.IsClosed)
            {
                entry.Window.PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
#if DEBUG
        notificationMilliseconds +=
            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                notificationStartedAt);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"transaction.group outcome=committed " +
            $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(groupStartedAt):F3} " +
            $"entriesMs={entryMilliseconds:F3} nativeCommitMs={nativeCommitMilliseconds:F3} " +
            $"completeMs={completionMilliseconds:F3} notificationsMs={notificationMilliseconds:F3} " +
            $"entries={entries.Length} slowest={slowestEntry}:{slowestEntryMilliseconds:F3}");
#endif
    }

    private EdgeCapsuleQueueProxyPlan? TryCreateEdgeCapsuleQueueProxyPlan(
        EdgeCapsuleVisualTransactionEntry[] entries)
    {
        if (entries.Length == 0 ||
            entries.Select(entry => entry.QueueKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            return null;
        }

        var candidates = new List<EdgeCapsuleQueueProxyCandidate>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Window.IsClosed)
            {
                return null;
            }
            var candidate = entry.Window.CaptureEdgeCapsuleQueueProxyCandidate(
                entry.QueueKey,
                entry.Motion);
            if (!candidate.HasValue)
            {
                return null;
            }
            candidates.Add(candidate.Value);
        }
        return EdgeCapsuleQueueProxyPolicy.TryCreate(
            entries[0].QueueKey,
            candidates);
    }

    private static bool CommitEdgeCapsuleQueueProxyLogicalEndpoints(
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

                var status = entry.Window.CommitEdgeCapsuleVisualTransaction(
                    EdgeCapsuleMotion.Snap(entry.Motion.Reason),
                    entry.RefreshLayout,
                    transactionTimestamp,
                    rebaseActiveTransition: true);
                committed &= status == EdgeCapsuleNativeBatchApplyStatus.Ready;
            }

            foreach (var entry in entries)
            {
                entry.Window.CompleteEdgeCapsuleVisualTransactionApply(
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
                entry.Window.PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
        return true;
    }

    private bool TryStartEdgeCapsuleQueueCompositionProxy(
        EdgeCapsuleQueueProxyPlan plan,
        EdgeCapsuleVisualTransactionEntry[] entries,
        out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        var sessionOrdinal =
            EdgeCapsuleQueueCompositionProxy.ReserveSessionOrdinal();
        var byPaperId = entries.ToDictionary(
            entry => entry.Window.EdgeCapsulePreviewPaperId,
            StringComparer.Ordinal);
        var members = new List<EdgeCapsuleQueueCompositionProxyMember>(
            plan.Members.Count);
        var started = false;
        try
        {
            foreach (var memberPlan in plan.Members)
            {
                if (!byPaperId.TryGetValue(memberPlan.PaperId, out var entry))
                {
                    return false;
                }

                EdgeCapsuleProxySnapshotHost? snapshotHost = null;
                if (memberPlan.Role == EdgeCapsuleQueueProxyMemberRole.OpeningPreview)
                {
#if DEBUG
                    var captureStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                    var snapshot = entry.Window.CaptureEdgeCapsuleQueueProxySnapshot(
                        memberPlan.Start);
#if DEBUG
                    var captureMilliseconds =
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            captureStartedAt);
                    var snapshotHostStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                    snapshotHost = snapshot == null
                        ? null
                        : EdgeCapsuleProxySnapshotHost.TryCreate(
                            snapshot,
                            memberPlan.Start);
#if DEBUG
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"proxy.snapshot session={sessionOrdinal} " +
                        $"cold={sessionOrdinal == 1} queue={plan.QueueKey} " +
                        $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(memberPlan.PaperId)} " +
                        $"captureMs={captureMilliseconds:F3} " +
                        $"hostCreateMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(snapshotHostStartedAt):F3} " +
                        $"outcome={(snapshotHost == null ? "failed" : "ready")} " +
                        $"pixels={(long)memberPlan.Start.Bounds.Width * memberPlan.Start.Bounds.Height}");
#endif
                    if (snapshotHost == null)
                    {
                        return false;
                    }
                }
                members.Add(new EdgeCapsuleQueueCompositionProxyMember(
                    entry.Window,
                    memberPlan,
                    entry.Window.EdgeCapsuleQueueProxySourceHandle,
                    snapshotHost));
            }

            var proxy = EdgeCapsuleQueueCompositionProxy.TryCreate(
                sessionOrdinal,
                plan,
                members,
                interactionRequested: (point, message) =>
                    CompleteAndRouteEdgeCapsuleQueueProxyInput(
                        plan.QueueKey,
                        point,
                        message),
                environmentChanged: () =>
                    CompleteEdgeCapsuleQueueCompositionProxy(
                        plan.QueueKey,
                        success: false),
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
                    $"cold={sessionOrdinal == 1} queue={plan.QueueKey} reason=startup-failed");
#endif
                return false;
            }

            // Publish routing before any Show/Host.Apply/Render work. WPF can re-enter while the
            // proxy is preparing endpoints; every such frame must already see the covered session.
            _edgeCapsuleQueueCompositionProxies[plan.QueueKey] = proxy;
            foreach (var member in members)
            {
                _edgeCapsuleQueueCompositionProxyByWindow[member.Window] = proxy;
            }
            started = true;
            if (!proxy.TryStart(out realHostMayHaveChanged))
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"proxy.session phase=fallback session={sessionOrdinal} " +
                    $"cold={sessionOrdinal == 1} queue={plan.QueueKey} reason=startup-failed");
#endif
                var finished = FinishEdgeCapsuleQueueCompositionProxy(
                    plan.QueueKey,
                    proxy,
                    success: false);
                // Finish always resolves and applies the latest real endpoint under the cover.
                // Any subsequent fallback must therefore snap rather than animate from stale start.
                realHostMayHaveChanged = true;
                // If the last cover must remain for a retry, keep this transaction on the proxy
                // path so the caller does not race a second native fallback through it.
                return !finished;
            }
            return true;
        }
        finally
        {
            if (!started)
            {
                foreach (var member in members)
                {
                    member.SnapshotHost?.Dispose();
                }
            }
        }
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

        var windows = _edgeCapsuleQueueCompositionProxyByWindow
            .Where(pair => ReferenceEquals(pair.Value, current))
            .Select(pair => pair.Key)
            .ToArray();

        if (current.CoverLost)
        {
            // A lost DComp device/output has no pixels left with which to cover a retry. Reveal
            // exact real source HWNDs first, then remove routing and let the ordinary WPF pipeline
            // replay the latest reducer endpoint. Stale geometry is preferable to an empty screen.
            var sourcesReleased = current.ReleaseAfterCoverLoss();
            if (!sourcesReleased)
            {
                // Keep this queue reserved while exact HWNDs are being recovered. The proxy now
                // reports no visual/input frame and allows real WPF applies through, but retaining
                // the route prevents a new proxy from racing the next 50 ms uncloak retry.
                current.ScheduleCompletionRetry(success: false);
                return false;
            }
            _edgeCapsuleQueueCompositionProxies.Remove(queueKey);
            foreach (var window in windows)
            {
                _edgeCapsuleQueueCompositionProxyByWindow.Remove(window);
            }
            try { current.Dispose(); } catch { current.ForceDisposeForShutdown(); }
            foreach (var window in windows)
            {
                if (!window.IsClosed)
                {
                    try { window.FlushEdgeCapsuleQueueProxyEndpoint(); } catch { }
                }
                try { window.ReleaseDeferredEdgeCapsuleQueueProxyPreviewContent(); } catch { }
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=emergency-release session={current.SessionOrdinal} " +
                $"cold={current.IsColdSession} queue={queueKey} members={windows.Length}");
#endif
            return true;
        }

        var endpointsReady = true;
#if DEBUG
        var handoffStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var endpointApplyStartedAt = handoffStartedAt;
#endif
        try
        {
            // Always resolve from the latest reducer/layout generation while the cover remains.
            // This handles display aborts and rapid state changes without replaying stale plan
            // targets. A closing host still keeps its live preview source until this point.
            foreach (var window in windows)
            {
                endpointsReady &= window.TryApplyLatestEdgeCapsuleQueueProxyEndpoint();
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
            current.ScheduleCompletionRetry(success: false);
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=handoff session={current.SessionOrdinal} " +
                $"cold={current.IsColdSession} queue={queueKey} " +
                $"members={windows.Length} requestedSuccess={success} " +
                $"ready=false retry=true " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointApplyStartedAt):F3}");
#endif
            return false;
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.endpoint phase=handoff session={current.SessionOrdinal} " +
            $"cold={current.IsColdSession} queue={queueKey} " +
            $"members={windows.Length} requestedSuccess={success} " +
            $"ready={endpointsReady} " +
            $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointApplyStartedAt):F3}");
#endif
        if (!current.TryReleaseForHandoff())
        {
            current.ScheduleCompletionRetry(success: false);
            return false;
        }
        try
        {
            _edgeCapsuleQueueCompositionProxies.Remove(queueKey);
            foreach (var window in windows)
            {
                _edgeCapsuleQueueCompositionProxyByWindow.Remove(window);
            }
        }
        finally
        {
            // Release already made real HWNDs authoritative. From here no optional WPF cleanup is
            // allowed to strand a route pointing at an empty proxy or skip native teardown.
            try { current.Dispose(); } catch { current.ForceDisposeForShutdown(); }
        }

        foreach (var window in windows)
        {
            try { window.ReleaseDeferredEdgeCapsuleQueueProxyPreviewContent(); } catch { }
            if (!window.IsClosed)
            {
                try { window.FlushEdgeCapsuleQueueProxyEndpoint(); } catch { }
            }
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.session phase=complete session={current.SessionOrdinal} " +
            $"cold={current.IsColdSession} queue={queueKey} " +
            $"members={windows.Length} requestedSuccess={success} " +
            $"endpointsReady={endpointsReady} " +
            $"handoffMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(handoffStartedAt):F3}");
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
        return !_edgeCapsuleQueueCompositionProxies.ContainsKey(queueKey);
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
            !_edgeCapsuleQueueCompositionProxies.TryGetValue(queueKey, out var remaining) ||
            !ReferenceEquals(remaining, proxy);
        if (hasTarget && handoffCompleted)
        {
            // The proxy consumes the physical down message. Replay exactly that down to the now
            // revealed endpoint; WPF then acquires normal mouse capture and all subsequent drag/
            // release messages follow the existing EdgeCapsuleHost input path.
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
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var proxy) &&
            proxy.Routes(window) &&
            proxy.TryRouteApply(window, frame))
        {
            // The Presenter has already snapped its authoritative business frame to the endpoint.
            // DirectComposition alone owns intermediate pixels; pointer/corridor callers obtain
            // the matching sampled visual frame through TryGet...ProxyPresentation.
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
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var proxy))
        {
            return proxy.TryGetPresentation(window, out frame);
        }
        frame = EdgeCapsulePresentationFrame.Hidden;
        return false;
    }

    internal bool TryGetEdgeCapsuleQueueProxyDiagnosticHandle(
        PaperWindow window,
        out IntPtr handle)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var proxy))
        {
            handle = proxy.OutputHandle;
            return handle != IntPtr.Zero;
        }
        handle = IntPtr.Zero;
        return false;
    }

    internal bool IsEdgeCapsuleQueueProxyRetainingSource(PaperWindow window) =>
        _edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
            window,
            out var proxy) &&
        proxy.RetainsSource(window);

    internal void CompleteEdgeCapsuleQueueCompositionProxyFor(
        PaperWindow window,
        bool success = false)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(
                window,
                out var proxy))
        {
            proxy.CompleteNow(success);
        }
    }

    private void DisposeEdgeCapsuleQueueCompositionProxies()
    {
        var proxies = _edgeCapsuleQueueCompositionProxies.Values
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
