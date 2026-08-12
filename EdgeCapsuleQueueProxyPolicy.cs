using System.Diagnostics;

namespace PaperTodo;

internal enum EdgeCapsuleQueueProxyMemberRole
{
    Moving,
    OpeningPreview,
    ClosingPreview
}

internal readonly record struct EdgeCapsuleQueueProxyCandidate(
    string PaperId,
    string QueueKey,
    EdgeCapsulePresentationFrame Start,
    EdgeCapsulePresentationFrame Target,
    EdgeCapsuleMotion Motion,
    bool HostReady,
    bool Topmost);

internal readonly record struct EdgeCapsuleQueueProxyMemberPlan(
    string PaperId,
    EdgeCapsulePresentationFrame Start,
    EdgeCapsulePresentationFrame Target,
    EdgeCapsuleQueueProxyMemberRole Role)
{
    public bool DefersRealEndpoint =>
        Role == EdgeCapsuleQueueProxyMemberRole.ClosingPreview;
}

internal sealed record EdgeCapsuleQueueProxyPlan(
    string QueueKey,
    DeviceScreenRect Envelope,
    EdgeCapsuleEdge Edge,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY,
    int DurationMilliseconds,
    bool Topmost,
    IReadOnlyList<EdgeCapsuleQueueProxyMemberPlan> Members);

/// <summary>
/// Pure admission and geometry policy for the transient queue compositor. The proxy covers only
/// source/target rectangles that actually change; it never reserves an entire monitor and never
/// changes the reducer's logical queue plan.
/// </summary>
internal static class EdgeCapsuleQueueProxyPolicy
{
    private const string EnabledEnvironmentVariable =
        "PAPERTODO_EDGE_QUEUE_COMPOSITION_PROXY";

    public static bool IsEnabled { get; } = ReadEnabledOverride();

    public static EdgeCapsuleQueueProxyPlan? TryCreate(
        string queueKey,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates)
    {
        if (!IsEnabled ||
            candidates.Count == 0 ||
            candidates.Any(candidate => !candidate.Topmost) ||
            candidates.Any(candidate =>
                !candidate.HostReady ||
                !candidate.Start.IsUsable ||
                !candidate.Target.IsUsable ||
                !candidate.Start.Visible ||
                !candidate.Target.Visible ||
                candidate.Start.Bounds.IsEmpty ||
                candidate.Target.Bounds.IsEmpty ||
                candidate.Motion.Kind != EdgeCapsuleMotionKind.Animate ||
                candidate.Start.Edge != candidate.Target.Edge ||
                candidate.Start.WallDeviceX != candidate.Target.WallDeviceX ||
                Math.Abs(candidate.Start.DpiScaleX - candidate.Target.DpiScaleX) > 0.001 ||
                Math.Abs(candidate.Start.DpiScaleY - candidate.Target.DpiScaleY) > 0.001 ||
                !string.Equals(candidate.QueueKey, queueKey, StringComparison.Ordinal)))
        {
            return null;
        }

        // The queue compositor is introduced for preview open/close/transfer and the placement
        // motion caused by those transactions. Other docked animations retain their existing
        // path until their input and drag semantics are migrated explicitly.
        if (!candidates.Any(candidate =>
                candidate.Motion.Reason == EdgeCapsuleTransitionReason.Preview))
        {
            return null;
        }

        var changed = candidates
            .Where(candidate => !FramesVisuallyMatch(candidate.Start, candidate.Target))
            .Select(candidate => new EdgeCapsuleQueueProxyMemberPlan(
                candidate.PaperId,
                candidate.Start,
                candidate.Target,
                RoleFor(candidate.Start, candidate.Target)))
            .ToArray();
        if (changed.Length == 0)
        {
            return null;
        }

        // Peer capsules affected by preview displacement are translation-only live surfaces. If a
        // peer also changes shape/content/opacity, applying its real endpoint underneath a wrapper
        // would mutate the very source being animated and can cause double scaling or a jump.
        if (changed.Any(member =>
                member.Role == EdgeCapsuleQueueProxyMemberRole.Moving &&
                !CanWrapMovingMemberLive(member.Start, member.Target)))
        {
            return null;
        }

        var first = changed[0];
        if (changed.Any(member =>
                member.Start.Edge != first.Start.Edge ||
                member.Start.WallDeviceX != first.Start.WallDeviceX ||
                Math.Abs(member.Start.DpiScaleX - first.Start.DpiScaleX) > 0.001 ||
                Math.Abs(member.Start.DpiScaleY - first.Start.DpiScaleY) > 0.001))
        {
            return null;
        }

        var envelope = default(DeviceScreenRect);
        foreach (var member in changed)
        {
            envelope = Union(envelope, member.Start.Bounds);
            envelope = Union(envelope, member.Target.Bounds);
        }
        if (envelope.IsEmpty)
        {
            return null;
        }

        return new EdgeCapsuleQueueProxyPlan(
            queueKey,
            envelope,
            first.Start.Edge,
            first.Start.WallDeviceX,
            first.Start.DpiScaleX,
            first.Start.DpiScaleY,
            Math.Max(
                1,
                candidates.Max(candidate => candidate.Motion.DurationMilliseconds)),
            Topmost: true,
            changed);
    }

    internal static EdgeCapsulePresentationFrame SampleLogicalFrame(
        EdgeCapsuleQueueProxyMemberPlan member,
        long startedAtTimestamp,
        int durationMilliseconds,
        long nowTimestamp)
    {
        var durationTicks = Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency * Math.Max(1, durationMilliseconds) / 1000.0));
        var transition = new EdgeCapsuleTransition(
            member.Start,
            new EdgeCapsuleTargetPresentation(
                member.Target.Visible,
                member.Target.Surface,
                member.Target.Bounds,
                member.Target.HostBounds,
                member.Target.InteractiveBounds,
                member.Target.Edge,
                member.Target.BodyWindowWidthDevice,
                member.Target.WallDeviceX,
                member.Target.DpiScaleX,
                member.Target.DpiScaleY,
                member.Target.MaximumCloseWidthDip,
                member.Target.Opacity,
                member.Target.ContentOpacity,
                member.Target.OutlineVisible,
                member.Target.IsHitTestVisible,
                member.Target.CloseSegmentActsAsContent),
            startedAtTimestamp,
            durationTicks,
            EdgeCapsuleTransitionReason.Preview);
        return EdgeCapsuleTransitionPolicy.Sample(transition, nowTimestamp).Frame;
    }

    private static EdgeCapsuleQueueProxyMemberRole RoleFor(
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target)
    {
        if (start.Surface != EdgeCapsuleSurfaceKind.DockedPreview &&
            target.Surface == EdgeCapsuleSurfaceKind.DockedPreview)
        {
            return EdgeCapsuleQueueProxyMemberRole.OpeningPreview;
        }
        if (start.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
            target.Surface != EdgeCapsuleSurfaceKind.DockedPreview)
        {
            return EdgeCapsuleQueueProxyMemberRole.ClosingPreview;
        }
        return EdgeCapsuleQueueProxyMemberRole.Moving;
    }

    private static bool FramesVisuallyMatch(
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target) => start == target;

    private static bool CanWrapMovingMemberLive(
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target) =>
        start.Surface == target.Surface &&
        start.Bounds.Width == target.Bounds.Width &&
        start.Bounds.Height == target.Bounds.Height &&
        start.BodyWindowWidthDevice == target.BodyWindowWidthDevice &&
        Math.Abs(start.Opacity - target.Opacity) < 0.001 &&
        Math.Abs(start.ContentOpacity - target.ContentOpacity) < 0.001 &&
        Math.Abs(start.MaximumCloseWidthDip - target.MaximumCloseWidthDip) < 0.001 &&
        start.OutlineVisible == target.OutlineVisible &&
        start.IsHitTestVisible == target.IsHitTestVisible &&
        start.CloseSegmentActsAsContent == target.CloseSegmentActsAsContent &&
        InteractiveBoundsTranslateWithVisual(start, target);

    private static bool InteractiveBoundsTranslateWithVisual(
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target)
    {
        if (start.InteractiveBounds.IsEmpty || target.InteractiveBounds.IsEmpty)
        {
            return start.InteractiveBounds.IsEmpty && target.InteractiveBounds.IsEmpty;
        }
        var deltaX = target.Bounds.Left - start.Bounds.Left;
        var deltaY = target.Bounds.Top - start.Bounds.Top;
        return target.InteractiveBounds == new DeviceScreenRect(
            start.InteractiveBounds.Left + deltaX,
            start.InteractiveBounds.Top + deltaY,
            start.InteractiveBounds.Right + deltaX,
            start.InteractiveBounds.Bottom + deltaY);
    }

    private static DeviceScreenRect Union(
        DeviceScreenRect first,
        DeviceScreenRect second)
    {
        if (first.IsEmpty)
        {
            return second;
        }
        if (second.IsEmpty)
        {
            return first;
        }
        return new DeviceScreenRect(
            Math.Min(first.Left, second.Left),
            Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right),
            Math.Max(first.Bottom, second.Bottom));
    }

    private static bool ReadEnabledOverride()
    {
        var value = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)
            ?.Trim()
            .ToLowerInvariant();
        return value is not ("0" or "false" or "off" or "none");
    }
}
