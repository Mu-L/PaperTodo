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

    internal bool HasExperimentalWindowAttachment =>
        _experimentalWindowAttachment != null;

    private bool HasExperimentalCapsuleMagnet =>
        _experimentalWindowAttachment?.Owner ==
        ExperimentalAttachmentOwner.CapsuleMagnet;

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

    internal void HandleExternalWindowEvent(ExternalWindowEvent windowEvent)
    {
        var session = _experimentalWindowAttachment;
        if (session == null ||
            session.TargetKind != ExperimentalAttachmentTargetKind.ExternalWindow ||
            session.ExternalWindow.Handle != windowEvent.Handle)
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

        if (session.Owner == ExperimentalAttachmentOwner.CapsuleMagnet &&
            (snapshot.IsMinimized || snapshot.IsCloaked || !snapshot.IsVisible))
        {
            DetachExperimentalWindowAttachment(savePosition: true);
            return;
        }

        ReconcileExperimentalWindowAttachment(snapshot);
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

        if (!ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot))
        {
            DetachExperimentalWindowAttachment(savePosition: true);
            return;
        }

        ReconcileExperimentalWindowAttachment(snapshot);
    }

    internal void DisableExperimentalCapsuleMagnet()
    {
        if (HasExperimentalCapsuleMagnet)
        {
            DetachExperimentalWindowAttachment(savePosition: true);
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DetachExperimentalWindowAttachment(bool savePosition)
    {
        if (_experimentalWindowAttachment == null)
        {
            return;
        }

        _experimentalWindowAttachment = null;
        if (savePosition &&
            _paper.IsCollapsed &&
            !HasDeepCapsuleSlotPlacement &&
            IsVisible)
        {
            SaveGeometryForCurrentPresentation();
        }
        RefreshExperimentalAttachmentMenus();
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
        if (session.Owner == ExperimentalAttachmentOwner.CapsuleMagnet &&
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
}
