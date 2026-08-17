using System.Diagnostics;
using System.Windows.Threading;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    public bool TryStart(out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        var started = false;
        try
        {
            started = PrepareAndStart();
            realHostMayHaveChanged = _realEndpointMutationStarted;
        }
        catch (Exception ex)
        {
            realHostMayHaveChanged = _realEndpointMutationStarted;
            Trace.TraceWarning(
                "Edge capsule queue DirectComposition proxy start failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        finally
        {
            _starting = false;
        }

        if (!started && !_coverPublished)
        {
            _ = TryRollbackInstalledRoot();
        }

        if (started && _completionPendingDuringStart)
        {
            var pendingSuccess = _pendingStartCompletionSuccess;
            _completionPendingDuringStart = false;
            _pendingStartCompletionSuccess = true;
            _ = _members[0].Window.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                (Action)(() => CompleteNow(pendingSuccess)));
        }
        return started;
    }

    public bool TryGetPresentation(
        PaperWindow window,
        out EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || _coverLost)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }

        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (member == null)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }

        frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
            member.Plan,
            AnimationStartedAtTimestamp,
            _plan.DurationMilliseconds,
            PresentationTimestamp);
        return true;
    }

    public bool TryGetSourcePresentation(
        PaperWindow window,
        out EdgeCapsulePresentationFrame frame)
    {
        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (_disposed || member == null)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }

        // Opening/moving generations prepare their real HWND at Target beneath the cover. A
        // conceal generation intentionally keeps the larger Source HWND alive until final handoff.
        frame = member.Plan.DefersRealEndpoint
            ? member.Plan.Source
            : member.Plan.Target;
        return frame.IsUsable;
    }

    public bool RetainsSource(PaperWindow window) =>
        !_disposed &&
        !_sourcesReleased &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window) &&
            member.SourceHandle != IntPtr.Zero);

    public bool Routes(PaperWindow window) =>
        !_disposed &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window));

    public IntPtr SourceHandleFor(PaperWindow window) =>
        _members.FirstOrDefault(member =>
            ReferenceEquals(member.Window, window))
            ?.SourceHandle ?? IntPtr.Zero;

    public bool TryReserveForSuccessor()
    {
        if (_disposed ||
            _starting ||
            _finishing ||
            _coverLost ||
            _sourcesReleased ||
            !_coverPublished ||
            _successorHeld)
        {
            return false;
        }

        _successorHeld = true;
        _heldAtTimestamp = 0;
        _completionPendingDuringSuccessorHold = false;
        _pendingSuccessorCompletionSuccess = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.successor phase=reserve session={_sessionOrdinal} " +
            $"queue={_plan.QueueKey} progress=" +
            $"{EdgeCapsuleQueueProxyPolicy.SampleProgress(AnimationStartedAtTimestamp, _plan.DurationMilliseconds, Stopwatch.GetTimestamp()):F4}");
#endif
        return true;
    }

    /// <summary>
    /// Latches one queue-wide presentation timestamp only after successor snapshot sources have
    /// been prepared. DirectComposition keeps advancing during the reservation, so the expensive
    /// WPF snapshot-host work cannot turn into a visible pause or a stale A-to-B start frame.
    /// </summary>
    public bool TryLatchForSuccessor()
    {
        if (_disposed ||
            _starting ||
            _finishing ||
            _coverLost ||
            _sourcesReleased ||
            !_coverPublished ||
            !_successorHeld ||
            _heldAtTimestamp != 0)
        {
            return false;
        }

        _heldAtTimestamp = Stopwatch.GetTimestamp();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.successor phase=latch session={_sessionOrdinal} " +
            $"queue={_plan.QueueKey} progress=" +
            $"{EdgeCapsuleQueueProxyPolicy.SampleProgress(AnimationStartedAtTimestamp, _plan.DurationMilliseconds, _heldAtTimestamp):F4}");
#endif
        return true;
    }

    public void CompleteAfterFailedSuccessor(bool success)
    {
        if (_disposed || !_successorHeld)
        {
            return;
        }

        var pendingCompletion =
            _completionPendingDuringSuccessorHold;
        var pendingSuccess =
            _pendingSuccessorCompletionSuccess && success;
        var heldAt = _heldAtTimestamp;
        _successorHeld = false;
        _heldAtTimestamp = 0;
        _completionPendingDuringSuccessorHold = false;
        _pendingSuccessorCompletionSuccess = true;

        if (pendingCompletion)
        {
            CompleteNow(pendingSuccess);
            return;
        }

        var durationTicks = Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency *
                Math.Max(1, _plan.DurationMilliseconds) /
                1000.0));
        // Reservation/latching only pauses the controller clock. The compositor animation keeps
        // running independently, so a rejected successor must rejoin its current time rather than
        // replaying the interval spent preparing the rejected generation.
        var elapsedTicks = Math.Max(
            0,
            Stopwatch.GetTimestamp() - AnimationStartedAtTimestamp);
        if (elapsedTicks >= durationTicks)
        {
            CompleteNow(success);
            return;
        }

        var remainingMilliseconds = Math.Max(
            1,
            (int)Math.Ceiling(
                (durationTicks - elapsedTicks) *
                1000.0 /
                Stopwatch.Frequency));
        _sampleTimer.Start();
        _completionTimer.Interval =
            TimeSpan.FromMilliseconds(
                remainingMilliseconds + 34);
        _completionTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.successor phase=resume session={_sessionOrdinal} " +
            $"queue={_plan.QueueKey} latched={heldAt > 0} " +
            $"remainingMs={remainingMilliseconds}");
#endif
    }

    public bool TryRouteApply(
        PaperWindow window,
        EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || _coverLost)
        {
            return false;
        }
        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (member == null)
        {
            return false;
        }

        // The Presenter remains the authority for the next target, but the current queue owner must
        // keep covering until the controller can stage its successor. Returning true here prevents a
        // re-entrant Presenter sample from moving the cloaked real HWND or completing predecessor A
        // before successor B has an exact start root ready.
        return true;
    }

    public bool TryResolveInputTarget(
        DeviceScreenPoint point,
        out IntPtr targetHandle,
        out DeviceScreenPoint endpointPoint)
    {
        if (_disposed || _coverLost)
        {
            targetHandle = IntPtr.Zero;
            endpointPoint = point;
            return false;
        }

        var now = PresentationTimestamp;
        foreach (var member in _members)
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                continue;
            }
            var current = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                member.Plan,
                AnimationStartedAtTimestamp,
                _plan.DurationMilliseconds,
                now);
            if (current.IsHitTestVisible &&
                !current.InteractiveBounds.IsEmpty &&
                EdgeCapsuleGeometry.Contains(
                    current.InteractiveBounds,
                    point))
            {
                targetHandle = member.SourceHandle;
                endpointPoint = MapPoint(
                    point,
                    current.InteractiveBounds,
                    member.Plan.Target.InteractiveBounds.IsEmpty
                        ? member.Plan.Target.Bounds
                        : member.Plan.Target.InteractiveBounds);
                return targetHandle != IntPtr.Zero;
            }
        }

        targetHandle = IntPtr.Zero;
        endpointPoint = point;
        return false;
    }

    private void HandleInteractionRequested(
        DeviceScreenPoint point,
        int message)
    {
        if (!_disposed && !_coverLost)
        {
            _interactionRequested(point, message);
        }
    }

    private void HandleEnvironmentChanged()
    {
        // Initial placement of a pooled output can raise WM_DPICHANGED. Exact physical output bounds
        // already own startup; subsequent monitor changes invalidate this immutable generation.
        if (!_disposed && !_starting)
        {
            _environmentChanged();
        }
    }

    private void HandleCompositionPaint()
    {
        if (_disposed || _sourcesReleased)
        {
            return;
        }
        try
        {
            using var baseDevice =
                _device.QueryInterface<IDCompositionDevice>();
            baseDevice.CheckDeviceState(out var valid).CheckError();
            if (valid)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue composition device check failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        HandleOutputLost();
    }

    private void HandleOutputLost()
    {
        _coverLost = true;
        CompleteNow(success: false);
    }

    private void HandleSharedRuntimeLost()
    {
        if (_disposed || _sourcesReleased || _coverLost)
        {
            return;
        }

        _coverLost = true;
        var dispatcher = _members[0].Window.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            CompleteNow(success: false);
            return;
        }

        // Runtime invalidation can be discovered while another queue is inside its completion
        // callback. Defer this queue's controller mutation, but mark cover loss immediately so no
        // new input/apply is routed through the failed compositor generation.
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            (Action)(() => CompleteNow(success: false)));
    }

    private static DeviceScreenPoint MapPoint(
        DeviceScreenPoint point,
        DeviceScreenRect source,
        DeviceScreenRect target)
    {
        if (source.IsEmpty || target.IsEmpty)
        {
            return point;
        }
        var relativeX = Math.Clamp(
            (point.X - source.Left) /
            Math.Max(1.0, source.Width),
            0,
            1);
        var relativeY = Math.Clamp(
            (point.Y - source.Top) /
            Math.Max(1.0, source.Height),
            0,
            1);
        return new DeviceScreenPoint(
            target.Left + relativeX * target.Width,
            target.Top + relativeY * target.Height);
    }

    public void CompleteNow(bool success)
    {
        if (_starting)
        {
            _completionPendingDuringStart = true;
            _pendingStartCompletionSuccess &= success;
            return;
        }
        if (_successorHeld)
        {
            _completionPendingDuringSuccessorHold = true;
            _pendingSuccessorCompletionSuccess &= success;
            return;
        }
        if (_disposed || _finishing)
        {
            return;
        }

        _finishing = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
        try
        {
            _completed(this, success);
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue proxy completion failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            ScheduleCompletionRetry(success: false);
        }
    }

    public void ScheduleCompletionRetry(bool success)
    {
        if (_disposed)
        {
            return;
        }
        if (_successorHeld)
        {
            _completionPendingDuringSuccessorHold = true;
            _pendingSuccessorCompletionSuccess &= success;
            return;
        }
        if (_sourcesReleased)
        {
            DisposeCore(clearTargetRoot: true);
            return;
        }

        _finishing = false;
        _completionRetrySuccess = success;
        _completionRetryCount++;
        _completionTimer.Stop();
        _completionTimer.Interval =
            TimeSpan.FromMilliseconds(50);
        _completionTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retry session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"attempt={_completionRetryCount} successTarget={success}");
#endif
    }
}
