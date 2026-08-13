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
        if (started && _completionPendingDuringStart)
        {
            var pendingSuccess = _pendingStartCompletionSuccess;
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
        if (_coverLost)
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
            Stopwatch.GetTimestamp());
        return true;
    }

    public bool RetainsSource(PaperWindow window) =>
        !_disposed &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window) &&
            member.SourceHandle != IntPtr.Zero);

    public bool Routes(PaperWindow window) =>
        !_disposed && _members.Any(member => ReferenceEquals(member.Window, window));

    public IntPtr SourceHandleFor(PaperWindow window) =>
        _members.FirstOrDefault(member => ReferenceEquals(member.Window, window))
            ?.SourceHandle ?? IntPtr.Zero;

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
        if (member.Plan.Target == frame)
        {
            return true;
        }

        QueueAbortAfterCurrentApply();
        return false;
    }

    public bool TryResolveInputTarget(
        DeviceScreenPoint point,
        out IntPtr targetHandle,
        out DeviceScreenPoint endpointPoint)
    {
        if (_coverLost)
        {
            targetHandle = IntPtr.Zero;
            endpointPoint = point;
            return false;
        }
        var now = Stopwatch.GetTimestamp();
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
                EdgeCapsuleGeometry.Contains(current.InteractiveBounds, point))
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

    private void HandleInteractionRequested(DeviceScreenPoint point, int message)
    {
        if (!_disposed && !_coverLost)
        {
            _interactionRequested(point, message);
        }
    }

    private void HandleEnvironmentChanged()
    {
        // Initial placement of a pooled output can raise WM_DPICHANGED. Exact physical output
        // bounds already own startup; later environment changes invalidate the immutable plan.
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
            using var baseDevice = _device.QueryInterface<IDCompositionDevice>();
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
            (point.X - source.Left) / Math.Max(1.0, source.Width),
            0,
            1);
        var relativeY = Math.Clamp(
            (point.Y - source.Top) / Math.Max(1.0, source.Height),
            0,
            1);
        return new DeviceScreenPoint(
            target.Left + relativeX * target.Width,
            target.Top + relativeY * target.Height);
    }

    private void QueueAbortAfterCurrentApply()
    {
        if (_abortQueued || _disposed || _finishing)
        {
            return;
        }
        _abortQueued = true;
        _ = _members[0].Window.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            (Action)(() =>
            {
                _abortQueued = false;
                CompleteNow(success: false);
            }));
    }

    public void CompleteNow(bool success)
    {
        if (_starting)
        {
            _completionPendingDuringStart = true;
            _pendingStartCompletionSuccess &= success;
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
        if (_sourcesReleased)
        {
            DisposeCore();
            return;
        }
        _finishing = false;
        _completionRetrySuccess = success;
        _completionRetryCount++;
        _completionTimer.Stop();
        _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _completionTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retry session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"attempt={_completionRetryCount} successTarget={success}");
#endif
    }
}
