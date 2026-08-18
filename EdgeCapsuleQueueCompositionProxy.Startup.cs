using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    public bool TryStart(out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        try
        {
            var started = PrepareAndStart();
            realHostMayHaveChanged = _realEndpointMutationStarted;
            return started;
        }
        finally
        {
            _starting = false;
            if (_completionPendingDuringStart)
            {
                CompleteNow(_pendingStartCompletionSuccess);
            }
        }
    }

    private bool PrepareAndStart()
    {
#if DEBUG
        var startedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        try
        {
            foreach (var member in _members)
            {
                if (member.Plan.Role !=
                        EdgeCapsuleQueueProxyMemberRole.MovingSource ||
                    !EdgeCapsuleQueueProxyPolicy.CanWrapMovingMemberLive(
                        member.Plan.Source,
                        member.Plan.Target))
                {
                    return false;
                }

                var sourceHost = member.Plan.Source.HostBounds;
                var startHost =
                    EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(
                        member.Plan.Start);
                var targetHost = member.Plan.Target.HostBounds;
                if (startHost.IsEmpty ||
                    sourceHost.Width != startHost.Width ||
                    sourceHost.Height != startHost.Height ||
                    sourceHost.Width != targetHost.Width ||
                    sourceHost.Height != targetHost.Height)
                {
                    return false;
                }

                var reference = _visuals.Count == 0
                    ? null
                    : _visuals[^1].Visual;
                _ = AddVisual(
                    member,
                    member.SourceHandle,
                    sourceHost,
                    startHost,
                    targetHost,
                    reference);
            }

            var successorHandles = _members
                .Select(member => member.SourceHandle)
                .Where(handle => handle != IntPtr.Zero)
                .ToHashSet();
            var predecessorHandles =
                _predecessor?.SnapshotCloakedSourceHandles() ??
                new HashSet<IntPtr>();
            var inheritedHandles = predecessorHandles
                .Where(successorHandles.Contains)
                .ToHashSet();
            var newHandles = successorHandles
                .Where(handle =>
                    !predecessorHandles.Contains(handle))
                .ToArray();
            var outgoingHandles = predecessorHandles
                .Where(handle =>
                    !successorHandles.Contains(handle))
                .ToArray();

            var cloakChanges =
                new List<WindowNative.WindowCloakChange>(
                    newHandles.Length + outgoingHandles.Length);
            cloakChanges.AddRange(newHandles.Select(handle =>
                new WindowNative.WindowCloakChange(
                    handle,
                    Cloaked: true,
                    RollbackCloaked: false)));
            cloakChanges.AddRange(outgoingHandles.Select(handle =>
                new WindowNative.WindowCloakChange(
                    handle,
                    Cloaked: false,
                    RollbackCloaked: true)));

            if (_predecessor == null &&
                !_window.Show(_outputBounds, _plan.Topmost))
            {
                return false;
            }

            var hostPromoted = false;
            long animationTimestamp = 0;

            bool PublishBeforeFlush()
            {
                if (!_host.Promote(this, _predecessor))
                {
                    return false;
                }
                hostPromoted = true;

                // Re-sample predecessor position immediately before root
                // replacement. Resource construction never freezes the visible
                // predecessor clock.
                animationTimestamp = Stopwatch.GetTimestamp();
                RebaseVisualStarts(animationTimestamp);
                ConfigureAnimations(animationTimestamp);

                _target.SetRoot(_root).CheckError();
                _device.Commit().CheckError();
                _targetRootInstalled = true;

                // Endpoint-once ownership: real HWNDs settle under the same
                // not-yet-flushed cloak transaction. WPF and DComp consume
                // one queue clock.
                _realEndpointMutationStarted = true;
                if (!_endpointCommitRequested(animationTimestamp))
                {
                    return false;
                }

                if (!_coverReady(this))
                {
                    return false;
                }
                _controllerPublished = true;
                return true;
            }

            void RollbackBeforeFlush()
            {
                Exception? rollbackFailure = null;
                if (_controllerPublished)
                {
                    try
                    {
                        _coverRollback(this);
                    }
                    catch (Exception ex)
                    {
                        rollbackFailure = ex;
                    }
                    _controllerPublished = false;
                }

                try
                {
                    if (_predecessor == null)
                    {
                        _target.SetRoot(null!).CheckError();
                    }
                    else
                    {
                        _target.SetRoot(
                            _predecessor._root).CheckError();
                    }
                    _device.Commit().CheckError();
                    _targetRootInstalled = false;
                }
                catch (Exception ex)
                {
                    rollbackFailure ??= ex;
                }

                if (hostPromoted &&
                    !_host.RollbackPromotion(this, _predecessor))
                {
                    rollbackFailure ??=
                        new InvalidOperationException(
                            "The queue host owner could not be restored.");
                }

                if (rollbackFailure != null)
                {
                    _coverLost = true;
                    throw rollbackFailure;
                }
            }

            var publication =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    cloakChanges,
                    PublishBeforeFlush,
                    RollbackBeforeFlush);
            if (publication !=
                WindowNative.WindowCloakBatchResult.Success)
            {
                if (publication ==
                    WindowNative.WindowCloakBatchResult.RollbackFailed)
                {
                    _coverLost = true;
                    foreach (var handle in
                             successorHandles.Concat(
                                 predecessorHandles))
                    {
                        _cloakedRealSourceHandles.Add(handle);
                    }
                    _ = ReleaseAfterCoverLoss();
                }
                return false;
            }

            _animationStartedAtTimestamp = animationTimestamp;
            foreach (var handle in successorHandles)
            {
                _cloakedRealSourceHandles.Add(handle);
            }
            if (_predecessor != null)
            {
                _predecessor
                    .CompleteSourceTransferAfterSuccessfulBoundary();
            }
            _coverPublished = true;

            _sampleTimer.Start();
            var elapsed = Stopwatch.GetElapsedTime(
                _animationStartedAtTimestamp,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            _completionTimer.Interval =
                TimeSpan.FromMilliseconds(Math.Max(
                    1,
                    _plan.DurationMilliseconds +
                    CompletionGuardMilliseconds -
                    elapsed));
            _completionTimer.Start();

#if DEBUG
            var outputPixels =
                (long)_outputBounds.Width * _outputBounds.Height;
            var wrappedPixels = _visuals.Sum(state =>
                (long)state.SourceBounds.Width *
                state.SourceBounds.Height);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=start mode=live-translation " +
                $"session={_sessionOrdinal} cold={IsColdSession} " +
                $"successor={_predecessor != null} " +
                $"queue={_plan.QueueKey} members={_members.Count} " +
                $"inherited={inheritedHandles.Count} " +
                $"revealed={outgoingHandles.Length} " +
                $"durationMs={_plan.DurationMilliseconds} " +
                $"output={_outputBounds.Left},{_outputBounds.Top}," +
                $"{_outputBounds.Width}x{_outputBounds.Height} " +
                $"prepareMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"resource.proxy mode=live-translation " +
                $"session={_sessionOrdinal} queue={_plan.QueueKey} " +
                $"outputPixels={outputPixels} " +
                $"wrappedPixels={wrappedPixels} " +
                $"snapshotHosts=0 clips=0 effects=0");
#endif
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule V3 Lite translation startup failed. " +
                "Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            return false;
        }
    }
}
