using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private bool ContainsVisual(DeviceScreenPoint point)
    {
        if (_coverLost)
        {
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        return _members.Any(member =>
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                return false;
            }
            var frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                member.Plan,
                AnimationStartedAtTimestamp,
                _plan.DurationMilliseconds,
                now);
            return frame.Visible &&
                frame.IsHitTestVisible &&
                !frame.InteractiveBounds.IsEmpty &&
                EdgeCapsuleGeometry.Contains(frame.InteractiveBounds, point);
        });
    }

    private long AnimationStartedAtTimestamp =>
        Volatile.Read(ref _animationStartedAtTimestamp) is var started && started > 0
            ? started
            : Stopwatch.GetTimestamp();

    private void OnSampleTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _finishing)
        {
            return;
        }
        foreach (var member in _members)
        {
            member.Window.InvalidateEdgeCapsuleQueueProxyPointer();
        }
    }

    private void OnCompletionTimerTick(object? sender, EventArgs e)
    {
        _completionTimer.Stop();
        CompleteNow(_completionRetrySuccess);
    }

    public bool TryReleaseForHandoff()
    {
        if (_disposed || _sourcesReleased)
        {
            return _sourcesReleased;
        }
        if (_coverLost)
        {
            return ReleaseAfterCoverLoss();
        }

        var restored = new List<IntPtr>(_cloakedRealSourceHandles.Count);
        var allRestored = true;
        foreach (var handle in _cloakedRealSourceHandles)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                continue;
            }
            if (WindowNative.TrySetWindowCloaked(handle, cloaked: false))
            {
                restored.Add(handle);
            }
            else
            {
                allRestored = false;
            }
        }
        if (!allRestored)
        {
            if (_coverLost)
            {
                WindowNative.FlushDesktopComposition();
                return false;
            }
            foreach (var handle in restored)
            {
                if (WindowNative.IsWindowHandleAlive(handle))
                {
                    _ = WindowNative.TrySetWindowCloaked(handle, cloaked: true);
                }
            }
            WindowNative.FlushDesktopComposition();
            return false;
        }

        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        try
        {
            // Keep the exact final proxy pixels over the newly uncloaked endpoints for one desktop
            // composition barrier. A bounded overlap is visually identical; an uncovered gap is
            // the edge flash reported by users. Only after DWM has accepted the real endpoints do
            // we hide the reusable output and detach its root.
            WindowNative.FlushDesktopComposition();
            _window.Hide();
            _target.SetRoot(null!).CheckError();
            _device.Commit().CheckError();
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue proxy release failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        return true;
    }

    public bool ReleaseAfterCoverLoss()
    {
        if (_disposed || _sourcesReleased)
        {
            return _sourcesReleased;
        }
        if (!TryRestoreSourcesAfterCoverLoss())
        {
            return false;
        }
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        try { _target.SetRoot(null!).CheckError(); } catch { }
        try { _device.Commit().CheckError(); } catch { }
        _window.Hide();
        WindowNative.FlushDesktopComposition();
        return true;
    }

    private bool TryRestoreSourcesAfterCoverLoss()
    {
        var allRestored = true;
        foreach (var handle in _cloakedRealSourceHandles)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                continue;
            }
            var restored = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (WindowNative.TrySetWindowCloaked(handle, cloaked: false))
                {
                    restored = true;
                    break;
                }
            }
            allRestored &= restored;
        }
        WindowNative.FlushDesktopComposition();
        return allRestored;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (!_sourcesReleased && !TryReleaseForHandoff())
        {
            return;
        }
        DisposeCore();
    }

    public void ForceDisposeForShutdown()
    {
        if (_disposed)
        {
            return;
        }
        _ = TryRestoreSourcesAfterCoverLoss();
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
        try
        {
            try { _target.SetRoot(null!).CheckError(); } catch { }
            try { _device.Commit().CheckError(); } catch { }
            foreach (var visual in _visuals)
            {
                try { visual.Dispose(); } catch { }
            }
            _visuals.Clear();
            try { _root.Dispose(); } catch { }
        }
        finally
        {
            foreach (var member in _members)
            {
                try { member.SnapshotHost?.Dispose(); } catch { }
            }
            _runtime.Release(
                _host,
                this,
                broken: _coverLost);
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=dispose session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"released={_sourcesReleased} reusedHost=true");
#endif
    }

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice2(
        IntPtr renderingDevice,
        ref Guid iid,
        out IntPtr dcompositionDevice);
}
