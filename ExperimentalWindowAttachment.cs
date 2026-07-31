using System.Windows.Controls;

namespace PaperTodo;

internal enum ExperimentalAttachmentOwner
{
    CapsuleMagnet,
    WindowTether
}

internal enum ExperimentalAttachmentTargetKind
{
    Screen,
    ExternalWindow
}

internal enum ExperimentalAttachmentEdge
{
    Left,
    Right,
    Top,
    Bottom
}

internal sealed record ExperimentalWindowAttachmentSession(
    ExperimentalAttachmentOwner Owner,
    ExperimentalAttachmentTargetKind TargetKind,
    ExperimentalAttachmentEdge Edge,
    bool InsideTarget,
    ExternalWindowIdentity ExternalWindow,
    string MonitorDeviceName,
    string TargetTitle,
    double PerpendicularOffsetDevice,
    double GapDip,
    DeviceScreenRect LastTargetBounds);

internal readonly record struct ExperimentalAttachmentPlan(
    ExperimentalWindowAttachmentSession Session,
    DeviceScreenRect WindowBounds,
    double ScoreDip);

internal static class ExperimentalWindowAttachmentGeometry
{
    public static bool TryPlanCapsuleMagnet(
        DeviceScreenRect capsuleBounds,
        MonitorGeometry monitor,
        IReadOnlyList<ExternalWindowSnapshot> externalWindows,
        bool includeScreenEdges,
        bool includeWindowEdges,
        double snapDistanceDip,
        double windowGapDip,
        out ExperimentalAttachmentPlan plan)
    {
        plan = default;
        if (capsuleBounds.IsEmpty ||
            !double.IsFinite(snapDistanceDip) ||
            snapDistanceDip <= 0)
        {
            return false;
        }

        var candidates = new List<ExperimentalAttachmentPlan>();
        if (includeScreenEdges)
        {
            AddScreenCandidates(
                candidates,
                capsuleBounds,
                monitor,
                snapDistanceDip);
        }

        if (includeWindowEdges)
        {
            foreach (var external in externalWindows)
            {
                AddExternalCandidates(
                    candidates,
                    capsuleBounds,
                    external,
                    snapDistanceDip,
                    windowGapDip);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        plan = candidates
            .OrderBy(candidate => candidate.ScoreDip)
            .ThenBy(candidate =>
                candidate.Session.TargetKind == ExperimentalAttachmentTargetKind.Screen
                    ? 0
                    : 1)
            .First();
        return true;
    }

    public static bool TryPlanWindowTether(
        DeviceScreenRect paperBounds,
        ExternalWindowSnapshot externalWindow,
        MonitorGeometry monitor,
        string preferredEdge,
        double gapDip,
        out ExperimentalAttachmentPlan plan)
    {
        plan = default;
        if (paperBounds.IsEmpty ||
            !externalWindow.IsUsableTarget ||
            monitor.WorkArea.IsEmpty ||
            !double.IsFinite(gapDip))
        {
            return false;
        }

        var normalizedEdge =
            ExperimentalWindowTetherOptions.NormalizeEdge(preferredEdge);
        ExperimentalAttachmentEdge[] edges = normalizedEdge switch
        {
            ExperimentalWindowTetherOptions.Left =>
                [ExperimentalAttachmentEdge.Left],
            ExperimentalWindowTetherOptions.Right =>
                [ExperimentalAttachmentEdge.Right],
            ExperimentalWindowTetherOptions.Top =>
                [ExperimentalAttachmentEdge.Top],
            ExperimentalWindowTetherOptions.Bottom =>
                [ExperimentalAttachmentEdge.Bottom],
            _ => Enum.GetValues<ExperimentalAttachmentEdge>()
        };
        var candidates = new List<ExperimentalAttachmentPlan>();
        foreach (var edge in edges)
        {
            AddTetherCandidate(
                candidates,
                paperBounds,
                externalWindow,
                monitor,
                edge,
                insideTarget: false,
                gapDip);
            AddTetherCandidate(
                candidates,
                paperBounds,
                externalWindow,
                monitor,
                edge,
                insideTarget: true,
                gapDip);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        plan = candidates
            .OrderBy(candidate => candidate.Session.InsideTarget ? 1 : 0)
            .ThenBy(candidate => candidate.ScoreDip)
            .First();
        return true;
    }

    public static DeviceScreenRect Resolve(
        ExperimentalWindowAttachmentSession session,
        DeviceScreenRect targetBounds,
        DeviceScreenRect currentWindowBounds,
        double dpiScale)
    {
        if (targetBounds.IsEmpty || currentWindowBounds.IsEmpty)
        {
            return default;
        }

        var gapDevice = Math.Max(
            0,
            RoundDevice(session.GapDip * Math.Max(1, dpiScale)));
        var width = currentWindowBounds.Width;
        var height = currentWindowBounds.Height;
        var offset = session.Edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? ClampOffset(
                    session.PerpendicularOffsetDevice,
                    targetBounds.Height,
                    height)
                : ClampOffset(
                    session.PerpendicularOffsetDevice,
                    targetBounds.Width,
                    width);

        var left = currentWindowBounds.Left;
        var top = currentWindowBounds.Top;
        switch (session.Edge)
        {
            case ExperimentalAttachmentEdge.Left:
                left = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Left + gapDevice
                        : targetBounds.Left - gapDevice - width;
                top = targetBounds.Top + RoundDevice(offset);
                break;
            case ExperimentalAttachmentEdge.Right:
                left = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Right - gapDevice - width
                        : targetBounds.Right + gapDevice;
                top = targetBounds.Top + RoundDevice(offset);
                break;
            case ExperimentalAttachmentEdge.Top:
                left = targetBounds.Left + RoundDevice(offset);
                top = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Top + gapDevice
                        : targetBounds.Top - gapDevice - height;
                break;
            case ExperimentalAttachmentEdge.Bottom:
                left = targetBounds.Left + RoundDevice(offset);
                top = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Bottom - gapDevice - height
                        : targetBounds.Bottom + gapDevice;
                break;
        }

        return new DeviceScreenRect(left, top, left + width, top + height);
    }

    public static bool FitsWorkArea(
        DeviceScreenRect bounds,
        DeviceScreenRect workArea) =>
        IsContainedBy(bounds, workArea);

    private static void AddScreenCandidates(
        ICollection<ExperimentalAttachmentPlan> candidates,
        DeviceScreenRect capsuleBounds,
        MonitorGeometry monitor,
        double snapDistanceDip)
    {
        if (monitor.WorkArea.IsEmpty)
        {
            return;
        }

        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Left,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleX,
            snapDistanceDip,
            gapDip: 0);
        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Right,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleX,
            snapDistanceDip,
            gapDip: 0);
        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Top,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleY,
            snapDistanceDip,
            gapDip: 0);
        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Bottom,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleY,
            snapDistanceDip,
            gapDip: 0);
    }

    private static void AddExternalCandidates(
        ICollection<ExperimentalAttachmentPlan> candidates,
        DeviceScreenRect capsuleBounds,
        ExternalWindowSnapshot external,
        double snapDistanceDip,
        double gapDip)
    {
        if (!external.IsUsableTarget)
        {
            return;
        }

        foreach (var edge in Enum.GetValues<ExperimentalAttachmentEdge>())
        {
            AddCandidate(
                candidates,
                ExperimentalAttachmentOwner.CapsuleMagnet,
                ExperimentalAttachmentTargetKind.ExternalWindow,
                edge,
                insideTarget: false,
                external.Identity,
                "",
                external.Title,
                capsuleBounds,
                external.Bounds,
                external.DpiScale,
                snapDistanceDip,
                gapDip);
            AddCandidate(
                candidates,
                ExperimentalAttachmentOwner.CapsuleMagnet,
                ExperimentalAttachmentTargetKind.ExternalWindow,
                edge,
                insideTarget: true,
                external.Identity,
                "",
                external.Title,
                capsuleBounds,
                external.Bounds,
                external.DpiScale,
                snapDistanceDip,
                gapDip);
        }
    }

    private static void AddTetherCandidate(
        ICollection<ExperimentalAttachmentPlan> candidates,
        DeviceScreenRect paperBounds,
        ExternalWindowSnapshot externalWindow,
        MonitorGeometry monitor,
        ExperimentalAttachmentEdge edge,
        bool insideTarget,
        double gapDip)
    {
        var perpendicularOffset = edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? paperBounds.Top - externalWindow.Bounds.Top
                : paperBounds.Left - externalWindow.Bounds.Left;
        var session = new ExperimentalWindowAttachmentSession(
            ExperimentalAttachmentOwner.WindowTether,
            ExperimentalAttachmentTargetKind.ExternalWindow,
            edge,
            insideTarget,
            externalWindow.Identity,
            monitor.DeviceName,
            externalWindow.Title,
            perpendicularOffset,
            Math.Max(0, gapDip),
            externalWindow.Bounds);
        var desired = Resolve(
            session,
            externalWindow.Bounds,
            paperBounds,
            externalWindow.DpiScale);
        if (desired.IsEmpty ||
            !IsContainedBy(desired, monitor.WorkArea) ||
            (insideTarget &&
             !CanFitInsideAttachmentAxis(
                 edge,
                 desired,
                 externalWindow.Bounds)))
        {
            return;
        }

        var deltaX = desired.Left - paperBounds.Left;
        var deltaY = desired.Top - paperBounds.Top;
        var scoreDip = Math.Sqrt(
            deltaX * (double)deltaX +
            deltaY * (double)deltaY) /
            Math.Max(1, externalWindow.DpiScale);
        candidates.Add(new ExperimentalAttachmentPlan(
            session,
            desired,
            scoreDip));
    }

    private static void AddCandidate(
        ICollection<ExperimentalAttachmentPlan> candidates,
        ExperimentalAttachmentOwner owner,
        ExperimentalAttachmentTargetKind targetKind,
        ExperimentalAttachmentEdge edge,
        bool insideTarget,
        ExternalWindowIdentity externalWindow,
        string monitorDeviceName,
        string targetTitle,
        DeviceScreenRect currentWindowBounds,
        DeviceScreenRect targetBounds,
        double dpiScale,
        double snapDistanceDip,
        double gapDip)
    {
        dpiScale = Math.Max(1, dpiScale);
        var perpendicularOffset = edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? currentWindowBounds.Top - targetBounds.Top
                : currentWindowBounds.Left - targetBounds.Left;
        var session = new ExperimentalWindowAttachmentSession(
            owner,
            targetKind,
            edge,
            insideTarget,
            externalWindow,
            monitorDeviceName,
            targetTitle,
            perpendicularOffset,
            gapDip,
            targetBounds);
        var desired = Resolve(
            session,
            targetBounds,
            currentWindowBounds,
            dpiScale);
        if (desired.IsEmpty)
        {
            return;
        }

        var axisDistanceDevice = edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? Math.Abs(desired.Left - currentWindowBounds.Left)
                : Math.Abs(desired.Top - currentWindowBounds.Top);
        var scoreDip = axisDistanceDevice / dpiScale;
        if (scoreDip > snapDistanceDip)
        {
            return;
        }

        if (!PerpendicularRangesTouch(
                edge,
                currentWindowBounds,
                targetBounds,
                RoundDevice(snapDistanceDip * dpiScale)))
        {
            return;
        }

        candidates.Add(new ExperimentalAttachmentPlan(
            session,
            desired,
            scoreDip));
    }

    private static bool PerpendicularRangesTouch(
        ExperimentalAttachmentEdge edge,
        DeviceScreenRect window,
        DeviceScreenRect target,
        int tolerance)
    {
        return edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? window.Bottom >= target.Top - tolerance &&
                  window.Top <= target.Bottom + tolerance
                : window.Right >= target.Left - tolerance &&
                  window.Left <= target.Right + tolerance;
    }

    private static bool IsContainedBy(
        DeviceScreenRect inner,
        DeviceScreenRect outer) =>
        inner.Left >= outer.Left &&
        inner.Top >= outer.Top &&
        inner.Right <= outer.Right &&
        inner.Bottom <= outer.Bottom;

    private static bool CanFitInsideAttachmentAxis(
        ExperimentalAttachmentEdge edge,
        DeviceScreenRect window,
        DeviceScreenRect target)
    {
        return edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? window.Width <= target.Width
                : window.Height <= target.Height;
    }

    private static double ClampOffset(
        double offset,
        int targetLength,
        int windowLength)
    {
        if (!double.IsFinite(offset))
        {
            return 0;
        }

        return Math.Clamp(
            offset,
            0,
            Math.Max(0, targetLength - windowLength));
    }

    private static int RoundDevice(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

public sealed partial class PaperWindow
{
    private ExperimentalWindowAttachmentSession? _experimentalWindowAttachment;
    private ExperimentalTetherCapsuleWindow? _experimentalTetherCapsule;
    private bool _experimentalTetherPresentationSuppressed;

    internal bool HasExperimentalWindowAttachment =>
        _experimentalWindowAttachment != null;

    internal bool HasExperimentalTetherCapsuleSurface =>
        _experimentalTetherCapsule?.IsVisible == true;

    private bool HasExperimentalCapsuleMagnet =>
        _experimentalWindowAttachment?.Owner ==
        ExperimentalAttachmentOwner.CapsuleMagnet;

    private bool HasExperimentalWindowTether =>
        _experimentalWindowAttachment?.Owner ==
        ExperimentalAttachmentOwner.WindowTether;

    private void DetachExperimentalAttachmentBeforeUserDrag()
    {
        if (_experimentalWindowAttachment != null)
        {
            DetachExperimentalWindowAttachment(savePosition: false);
        }
    }

    private void TryAttachExperimentalCapsuleMagnetAfterDrag()
    {
        if (!_controller.State.ExperimentalCapsuleMagnetism ||
            !_paper.IsCollapsed ||
            HasDeepCapsuleSlotPlacement ||
            IsPaperFormTransitioning ||
            !IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var capsuleBounds))
        {
            return;
        }

        var center = new DeviceScreenPoint(
            capsuleBounds.Left + capsuleBounds.Width / 2.0,
            capsuleBounds.Top + capsuleBounds.Height / 2.0);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                center,
                this,
                out var monitor))
        {
            return;
        }

        var externalTargets =
            _controller.State.ExperimentalCapsuleMagnetWindowEdges
                ? ExternalWindowNative.EnumerateTargets(maximumCount: 40)
                : Array.Empty<ExternalWindowSnapshot>();
        if (!ExperimentalWindowAttachmentGeometry.TryPlanCapsuleMagnet(
                capsuleBounds,
                monitor,
                externalTargets,
                _controller.State.ExperimentalCapsuleMagnetScreenEdges,
                _controller.State.ExperimentalCapsuleMagnetWindowEdges,
                _controller.State.ExperimentalCapsuleMagnetDistance,
                ExperimentalWindowAttachmentOptions.DefaultWindowGap,
                out var plan))
        {
            return;
        }

        _experimentalWindowAttachment = plan.Session;
        ApplyExperimentalAttachmentBounds(plan.WindowBounds);
        SaveGeometryForCurrentPresentation();
        RefreshExperimentalAttachmentMenus();
    }

    private void AttachExperimentalWindowTether(
        ExternalWindowIdentity identity)
    {
        if (!_controller.State.ExperimentalWindowTethering ||
            _paper.IsCollapsed ||
            HasDeepCapsuleSlotPlacement ||
            IsPaperFormTransitioning ||
            WindowState != System.Windows.WindowState.Normal ||
            _isSnappedPresentation ||
            !IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var paperBounds) ||
            !ExternalWindowNative.TryGetSnapshot(identity, out var target) ||
            !target.IsUsableTarget)
        {
            return;
        }

        var targetCenter = new DeviceScreenPoint(
            target.Bounds.Left + target.Bounds.Width / 2.0,
            target.Bounds.Top + target.Bounds.Height / 2.0);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                targetCenter,
                this,
                out var monitor) ||
            !ExperimentalWindowAttachmentGeometry.TryPlanWindowTether(
                paperBounds,
                target,
                monitor,
                _controller.State.ExperimentalWindowTetherPreferredEdge,
                _controller.State.ExperimentalWindowTetherGap,
                out var plan))
        {
            return;
        }

        DetachExperimentalWindowAttachment(savePosition: false);
        _experimentalWindowAttachment = plan.Session;
        ApplyExperimentalAttachmentBounds(plan.WindowBounds);
        SaveGeometryForCurrentPresentation();
        RefreshExperimentalAttachmentMenus();
    }

    internal void HandleExternalWindowEvent(ExternalWindowEvent windowEvent)
    {
        var session = _experimentalWindowAttachment;
        if (session == null ||
            session.TargetKind != ExperimentalAttachmentTargetKind.ExternalWindow)
        {
            return;
        }

        var visibilityLinked =
            session.Owner == ExperimentalAttachmentOwner.WindowTether &&
            _controller.State.ExperimentalTetherVisibilityLink;
        if (visibilityLinked &&
            (windowEvent.Kind & ExternalWindowEventKind.Foreground) != 0 &&
            ExternalWindowNative.IsSameProcess(
                session.ExternalWindow,
                windowEvent.Handle) &&
            ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var foregroundTarget) &&
            foregroundTarget.IsUsableTarget)
        {
            ReconcileExperimentalWindowAttachment(foregroundTarget);
            RestoreExperimentalTetherPresentation();
        }

        if (session.ExternalWindow.Handle != windowEvent.Handle)
        {
            return;
        }

        if ((windowEvent.Kind & ExternalWindowEventKind.Destroyed) != 0 ||
            !ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot))
        {
            DetachExperimentalWindowAttachment(savePosition: true);
            return;
        }

        var targetUnavailable =
            snapshot.IsMinimized ||
            snapshot.IsCloaked ||
            !snapshot.IsVisible;
        var targetBecameUnavailable =
            (windowEvent.Kind &
             (ExternalWindowEventKind.MinimizeStarted |
              ExternalWindowEventKind.Cloaked)) != 0;
        if (visibilityLinked &&
            (targetUnavailable || targetBecameUnavailable))
        {
            SuppressExperimentalTetherPresentation(snapshot);
            return;
        }

        if (targetUnavailable)
        {
            if (session.Owner == ExperimentalAttachmentOwner.CapsuleMagnet)
            {
                DetachExperimentalWindowAttachment(savePosition: true);
            }
            return;
        }

        ReconcileExperimentalWindowAttachment(snapshot);
        if (visibilityLinked)
        {
            RestoreExperimentalTetherPresentation();
        }
    }

    internal void RefreshExperimentalAttachmentForDisplayMetrics()
    {
        var session = _experimentalWindowAttachment;
        if (session == null)
        {
            return;
        }

        if (session.TargetKind == ExperimentalAttachmentTargetKind.Screen)
        {
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    session.MonitorDeviceName,
                    this,
                    out var monitor) ||
                !WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds))
            {
                DetachExperimentalWindowAttachment(savePosition: true);
                return;
            }

            var desired = ExperimentalWindowAttachmentGeometry.Resolve(
                session,
                monitor.WorkArea,
                currentBounds,
                session.Edge is
                    ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                        ? monitor.DpiScaleX
                        : monitor.DpiScaleY);
            _experimentalWindowAttachment = session with
            {
                LastTargetBounds = monitor.WorkArea
            };
            ApplyExperimentalAttachmentBounds(desired);
            return;
        }

        HandleExternalWindowEvent(new ExternalWindowEvent(
            session.ExternalWindow.Handle,
            ExternalWindowEventKind.Location));
    }

    internal void DisableExperimentalCapsuleMagnet()
    {
        if (HasExperimentalCapsuleMagnet)
        {
            DetachExperimentalWindowAttachment(savePosition: true);
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DisableExperimentalWindowTether()
    {
        if (HasExperimentalWindowTether)
        {
            DetachExperimentalWindowAttachment(savePosition: true);
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DisableExperimentalTetherVisibilityLink()
    {
        RestoreExperimentalTetherPresentation();
    }

    internal void RefreshExperimentalTetherVisibilityOptions()
    {
        if (!_controller.State.ExperimentalTetherVisibilityLink)
        {
            RestoreExperimentalTetherPresentation();
            return;
        }

        if (!_experimentalTetherPresentationSuppressed)
        {
            var session = _experimentalWindowAttachment;
            if (session?.Owner == ExperimentalAttachmentOwner.WindowTether &&
                ExternalWindowNative.TryGetSnapshot(
                    session.ExternalWindow,
                    out var snapshot) &&
                (snapshot.IsMinimized ||
                 snapshot.IsCloaked ||
                 !snapshot.IsVisible))
            {
                SuppressExperimentalTetherPresentation(snapshot);
            }
            return;
        }

        CloseExperimentalTetherCapsule();
        if (_controller.State.ExperimentalTetherMinimizedBehavior ==
            ExperimentalTetherVisibilityModes.Capsule)
        {
            ShowExperimentalTetherCapsule();
        }
    }

    internal void RefreshExperimentalWindowTetherOptions()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether ||
            !ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot) ||
            !snapshot.IsUsableTarget ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds) ||
            !TryGetTargetMonitor(snapshot, out var monitor) ||
            !ExperimentalWindowAttachmentGeometry.TryPlanWindowTether(
                currentBounds,
                snapshot,
                monitor,
                _controller.State.ExperimentalWindowTetherPreferredEdge,
                _controller.State.ExperimentalWindowTetherGap,
                out var plan))
        {
            return;
        }

        _experimentalWindowAttachment = plan.Session;
        ApplyExperimentalAttachmentBounds(plan.WindowBounds);
        SaveGeometryForCurrentPresentation();
    }

    internal void DetachExperimentalWindowAttachment(bool savePosition)
    {
        var session = _experimentalWindowAttachment;
        if (session == null)
        {
            return;
        }

        if (session.Owner == ExperimentalAttachmentOwner.WindowTether)
        {
            RestoreExperimentalTetherPresentation();
        }
        _experimentalWindowAttachment = null;
        if (savePosition &&
            !HasDeepCapsuleSlotPlacement &&
            IsVisible)
        {
            SaveGeometryForCurrentPresentation();
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DisposeExperimentalWindowAttachment()
    {
        CancelExperimentalTetherPresentation(showMain: false);
        _experimentalWindowAttachment = null;
    }

    internal void RestoreExperimentalTetherPresentationForExplicitShow()
    {
        RestoreExperimentalTetherPresentation();
    }

    private void ReconcileExperimentalWindowAttachment(
        ExternalWindowSnapshot snapshot)
    {
        var session = _experimentalWindowAttachment;
        if (session == null ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds))
        {
            return;
        }

        var desired = ExperimentalWindowAttachmentGeometry.Resolve(
            session,
            snapshot.Bounds,
            currentBounds,
            snapshot.DpiScale);
        if (session.Owner == ExperimentalAttachmentOwner.WindowTether &&
            TryGetTargetMonitor(snapshot, out var monitor) &&
            !ExperimentalWindowAttachmentGeometry.FitsWorkArea(
                desired,
                monitor.WorkArea) &&
            ExperimentalWindowAttachmentGeometry.TryPlanWindowTether(
                currentBounds,
                snapshot,
                monitor,
                _controller.State.ExperimentalWindowTetherPreferredEdge,
                _controller.State.ExperimentalWindowTetherGap,
                out var replanned))
        {
            session = replanned.Session;
            desired = replanned.WindowBounds;
        }
        else if (session.Owner == ExperimentalAttachmentOwner.CapsuleMagnet &&
            !FitsAnyConnectedMonitor(desired))
        {
            var visibleSession = session with
            {
                InsideTarget = true
            };
            var visibleDesired =
                ExperimentalWindowAttachmentGeometry.Resolve(
                    visibleSession,
                    snapshot.Bounds,
                    currentBounds,
                    snapshot.DpiScale);
            if (!FitsAnyConnectedMonitor(visibleDesired))
            {
                DetachExperimentalWindowAttachment(savePosition: true);
                return;
            }

            session = visibleSession;
            desired = visibleDesired;
        }
        _experimentalWindowAttachment = session with
        {
            LastTargetBounds = snapshot.Bounds,
            TargetTitle = snapshot.Title
        };
        ApplyExperimentalAttachmentBounds(desired);
    }

    private static bool FitsAnyConnectedMonitor(
        DeviceScreenRect bounds)
    {
        return WindowWorkAreaHelper.ConnectedMonitorGeometries()
            .Any(monitor =>
                bounds.Left >= monitor.WorkArea.Left &&
                bounds.Top >= monitor.WorkArea.Top &&
                bounds.Right <= monitor.WorkArea.Right &&
                bounds.Bottom <= monitor.WorkArea.Bottom);
    }

    private bool TryGetTargetMonitor(
        ExternalWindowSnapshot snapshot,
        out MonitorGeometry monitor)
    {
        var center = new DeviceScreenPoint(
            snapshot.Bounds.Left + snapshot.Bounds.Width / 2.0,
            snapshot.Bounds.Top + snapshot.Bounds.Height / 2.0);
        return WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            center,
            this,
            out monitor);
    }

    private void ApplyExperimentalAttachmentBounds(DeviceScreenRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        MoveWindowWithoutGeometrySave(() =>
            WindowNative.TryMoveWindowDevicePosition(
                this,
                new DeviceScreenPoint(bounds.Left, bounds.Top)));
    }

    private void SuppressExperimentalTetherPresentation(
        ExternalWindowSnapshot snapshot)
    {
        if (_experimentalTetherPresentationSuppressed ||
            !HasExperimentalWindowTether ||
            !_paper.IsVisible ||
            !IsVisible ||
            _windowLifecycle != PaperWindowLifecycleState.Alive)
        {
            return;
        }

        if (snapshot.IsUsableTarget)
        {
            ReconcileExperimentalWindowAttachment(snapshot);
        }
        SaveGeometryForCurrentPresentation();
        _experimentalTetherPresentationSuppressed = true;
        MoveWindowWithoutGeometrySave(Hide);
        if (_controller.State.ExperimentalTetherMinimizedBehavior ==
            ExperimentalTetherVisibilityModes.Capsule)
        {
            ShowExperimentalTetherCapsule();
        }
    }

    private void ShowExperimentalTetherCapsule()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether ||
            !_experimentalTetherPresentationSuppressed)
        {
            return;
        }

        CloseExperimentalTetherCapsule();
        var restingOpacity =
            _controller.State.ExperimentalRestingCapsuleOpacity
                ? ExperimentalOpacityLevels.Normalize(
                    _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                    ExperimentalOpacityLevels.DefaultRestingCapsule)
                : 1.0;
        var capsule = new ExperimentalTetherCapsuleWindow(
            Strings.Format(
                "LabsTetherCapsuleLabelFormat",
                _controller.PaperCapsuleTitle(_paper)),
            Strings.Format(
                "LabsTetherCapsuleTargetTipFormat",
                session.TargetTitle),
            ActivateExperimentalTetherTarget,
            normalTopmost: true,
            restingOpacity: restingOpacity);
        _experimentalTetherCapsule = capsule;
        capsule.UnexpectedlyClosed += (_, _) =>
        {
            if (!ReferenceEquals(_experimentalTetherCapsule, capsule))
            {
                return;
            }

            _experimentalTetherCapsule = null;
            RestoreExperimentalTetherPresentation();
        };
        capsule.SetExperimentalPassive(IsExperimentalAllSurfacesPassive);

        var anchorBounds = WindowNative.TryGetWindowDeviceBounds(
                this,
                out var currentBounds)
            ? currentBounds
            : session.LastTargetBounds;
        capsule.SetFullscreenAvoidance(
            _controller.FullscreenAvoidanceWindowFor(this));
        capsule.ShowAt(anchorBounds);
        capsule.SetFullscreenAvoidance(
            _controller.FullscreenAvoidanceWindowFor(capsule));
    }

    private void ActivateExperimentalTetherTarget()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether)
        {
            CancelExperimentalTetherPresentation(showMain: true);
            return;
        }

        if (!ExternalWindowNative.RestoreAndActivate(
                session.ExternalWindow))
        {
            DetachExperimentalWindowAttachment(savePosition: true);
            return;
        }

        RestoreExperimentalTetherPresentation();
    }

    private void RestoreExperimentalTetherPresentation()
    {
        CancelExperimentalTetherPresentation(showMain: true);
    }

    private void CancelExperimentalTetherPresentation(bool showMain)
    {
        var wasSuppressed = _experimentalTetherPresentationSuppressed;
        _experimentalTetherPresentationSuppressed = false;
        CloseExperimentalTetherCapsule();
        if (!wasSuppressed ||
            !showMain ||
            !_paper.IsVisible ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsVisible)
        {
            return;
        }

        var showActivated = ShowActivated;
        ShowActivated = false;
        try
        {
            MoveWindowWithoutGeometrySave(Show);
        }
        finally
        {
            ShowActivated = showActivated;
        }
        RefreshEffectiveTopmost();
        WindowNative.BringToFrontNoActivate(this);
    }

    private void CloseExperimentalTetherCapsule()
    {
        var capsule = _experimentalTetherCapsule;
        _experimentalTetherCapsule = null;
        capsule?.CloseForOwner();
    }

    private void RefreshExperimentalAttachmentMenus()
    {
        if (!_isShellBuilt)
        {
            return;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
    }

    internal void RefreshExperimentalAttachmentMenu()
    {
        RefreshExperimentalAttachmentMenus();
    }

    private MenuItem BuildExperimentalWindowTetherMenu()
    {
        var root = new MenuItem
        {
            Header = Strings.Get("LabsWindowTetherChoose"),
            Padding = new System.Windows.Thickness(8, 4, 10, 4),
            Background = System.Windows.Media.Brushes.Transparent
        };
        root.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            "TextBrushKey");
        root.Items.Add(MenuHeader(Strings.Get("LabsWindowTetherOpenToChoose")));
        root.SubmenuOpened += (_, _) =>
        {
            root.Items.Clear();
            var targets = ExternalWindowNative.EnumerateTargets(maximumCount: 24);
            if (targets.Count == 0)
            {
                root.Items.Add(MenuHeader(
                    Strings.Get("LabsWindowTetherNoTargets")));
                return;
            }

            foreach (var target in targets)
            {
                var title = target.Title.Length <= 60
                    ? target.Title
                    : target.Title[..57] + "…";
                var item = MenuItem(
                    title,
                    (_, _) => AttachExperimentalWindowTether(
                        target.Identity));
                item.ToolTip = target.Title;
                root.Items.Add(item);
            }
        };
        return root;
    }
}
