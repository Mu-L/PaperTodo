using System.Diagnostics;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private bool PrepareAndStart()
    {
#if DEBUG
        var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        try
        {
  foreach (var member in _members)
  {
      if (member.Plan.Role != EdgeCapsuleQueueProxyMemberRole.MovingSource ||
          !EdgeCapsuleQueueProxyPolicy.CanWrapMovingMemberLive(
              member.Plan.Source,
              member.Plan.Target))
      {
          return false;
      }

      var sourceHost = member.Plan.Source.HostBounds;
      var startHost = EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(
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

      var reference = _visuals.Count == 0 ? null : _visuals[^1].Visual;
      _ = AddVisual(
          member,
          EdgeCapsuleQueueProxyVisualLayer.MovingSource,
          member.SourceHandle,
          sourceHost,
          startHost,
          targetHost,
          EdgeCapsuleQueueProxyGeometry.FullClip(sourceHost),
          EdgeCapsuleQueueProxyGeometry.FullClip(sourceHost),
          1,
          1,
          reference);
  }

  var cloakChanges = new List<WindowNative.WindowCloakChange>();
  foreach (var member in _members)
  {
      var inherited = _predecessor?.RetainsSource(member.Window) == true;
      if (!inherited && _cloakedRealSourceHandles.Add(member.SourceHandle))
      {
          cloakChanges.Add(new WindowNative.WindowCloakChange(
              member.SourceHandle,
              Cloaked: true,
              RollbackCloaked: false));
      }
  }

  _target.SetRoot(_root).CheckError();
  _device.Commit().CheckError();
  _targetRootInstalled = true;

  if (_predecessor == null && !_window.Show(_outputBounds, _plan.Topmost))
  {
      return false;
  }

  var coverPublished = cloakChanges.Count > 0
      ? WindowNative.TrySetWindowCloakedBatch(cloakChanges)
      : WindowNative.TryFlushDesktopComposition();
  if (!coverPublished || _coverLost)
  {
      return false;
  }

  if (!_host.Promote(this, _predecessor) || !_coverReady(this))
  {
      return false;
  }
  _coverPublished = true;

  // Komorebi-style endpoint-once ownership: publish the cover first, then move
  // every real HWND to its final host exactly once. Unlike a ghost resize, the
  // wrapped surface stays the same size; WPF alone morphs its live contents.
  _realEndpointMutationStarted = true;
  _animationStartedAtTimestamp = Stopwatch.GetTimestamp();
  if (!_endpointCommitRequested(_animationStartedAtTimestamp))
  {
      return false;
  }

  try
  {
      _members[0].Window.Dispatcher.Invoke(
          static () => { },
          DispatcherPriority.Render);
  }
  catch
  {
      return false;
  }

  ConfigureAnimations(_animationStartedAtTimestamp);
  _device.Commit().CheckError();
  WindowNative.FlushDesktopComposition();

  _sampleTimer.Start();
  var elapsed = Stopwatch.GetElapsedTime(
      _animationStartedAtTimestamp,
      Stopwatch.GetTimestamp()).TotalMilliseconds;
  _completionTimer.Interval = TimeSpan.FromMilliseconds(
      Math.Max(
          1,
          _plan.DurationMilliseconds + CompletionGuardMilliseconds - elapsed));
  _completionTimer.Start();
#if DEBUG
  var outputPixels = (long)_outputBounds.Width * _outputBounds.Height;
  var wrappedPixels = _visuals.Sum(state =>
      (long)state.SourceBounds.Width * state.SourceBounds.Height);
  EdgeCapsulePerformanceDiagnostics.Trace(
      $"proxy.session phase=start mode=live-translation session={_sessionOrdinal} " +
      $"cold={IsColdSession} successor={_predecessor != null} " +
      $"queue={_plan.QueueKey} members={_members.Count} " +
      $"durationMs={_plan.DurationMilliseconds} " +
      $"output={_outputBounds.Left},{_outputBounds.Top}," +
      $"{_outputBounds.Width}x{_outputBounds.Height} " +
      $"prepareMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
  EdgeCapsulePerformanceDiagnostics.Trace(
      $"resource.proxy mode=live-translation session={_sessionOrdinal} " +
      $"queue={_plan.QueueKey} outputPixels={outputPixels} " +
      $"wrappedPixels={wrappedPixels} snapshotHosts=0");
#endif
  return true;
        }
        catch (Exception ex)
        {
  Trace.TraceWarning(
      "Edge capsule V3 Lite translation startup failed. Queue={0}; Session={1}; Exception={2}",
      _plan.QueueKey,
      _sessionOrdinal,
      ex);
  return false;
        }
    }
}
