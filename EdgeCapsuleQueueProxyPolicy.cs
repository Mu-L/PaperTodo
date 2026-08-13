using System.Diagnostics;

namespace PaperTodo;

internal enum EdgeCapsuleQueueProxyMemberRole
{
    Moving = 0,
    // Opening a preview and resizing the compact hover shell use the same visual contract: freeze
    // the old pixels, prepare the real endpoint under cover, then crossfade both wall-anchored layers.
    SnapshotMorph = 1,
    OpeningPreview = SnapshotMorph,
    ResizingShell = SnapshotMorph,
    ClosingPreview = 2
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

    public bool RequiresStartSnapshot =>
        Role == EdgeCapsuleQueueProxyMemberRole.SnapshotMorph;

    public bool UsesEndpointLayer => RequiresStartSnapshot;
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
/// Admission and geometry policy for the queue compositor. Preview transactions and the compact
/// shell's pointer-driven resize share the same compositor owner. Unchanged queue bookkeeping can
/// never silently veto a session; every changed pixel owner is validated explicitly.
/// </summary>
internal static class EdgeCapsuleQueueProxyPolicy
{
    // V2.5 is now the production edge-animation path. Historical A/B branches remain available;
    // a persisted process environment variable must not silently restore per-frame HWND motion.
    public static bool IsEnabled => true;

    public static EdgeCapsuleQueueProxyPlan? TryCreate(
        string queueKey,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return Reject(queueKey, "no-candidates", candidates);
        }

        var supportedReason = candidates.Any(candidate =>
            candidate.Motion.Reason is
                EdgeCapsuleTransitionReason.Preview or
                EdgeCapsuleTransitionReason.Pointer);
        if (!supportedReason)
        {
            return Reject(queueKey, "unsupported-transaction", candidates);
        }

        var changedCandidates = candidates
            .Where(candidate => !FramesVisuallyMatch(candidate.Start, candidate.Target))
            .ToArray();
        if (changedCandidates.Length == 0)
        {
            return Reject(queueKey, "no-visual-change", candidates);
        }

        foreach (var candidate in changedCandidates)
        {
            var rejection = ChangedCandidateRejection(candidate, queueKey);
            if (rejection != null)
            {
                return Reject(queueKey, rejection, candidates, candidate);
            }
        }

        var changed = changedCandidates
            .Select(candidate => new EdgeCapsuleQueueProxyMemberPlan(
                candidate.PaperId,
                candidate.Start,
                candidate.Target,
                RoleFor(candidate)))
            .ToArray();

        // Preview queue peers remain intentionally conservative: only translation-only live
        // surfaces are admitted. Pointer-driven compact shell resize is the explicit exception and
        // is promoted to SnapshotMorph, where the live HWND is never mutated beneath itself.
        var unsupportedMovingMember = changed.FirstOrDefault(member =>
            member.Role == EdgeCapsuleQueueProxyMemberRole.Moving &&
            !CanWrapMovingMemberLive(member.Start, member.Target));
        if (!string.IsNullOrEmpty(unsupportedMovingMember.PaperId))
        {
            return Reject(
                queueKey,
                "moving-member-not-translation-only",
                candidates,
                changedCandidates.First(candidate =>
                    string.Equals(
                        candidate.PaperId,
                        unsupportedMovingMember.PaperId,
                        StringComparison.Ordinal)));
        }

        var first = changed[0];
        var mismatchedGeometry = changedCandidates.FirstOrDefault(candidate =>
            candidate.Start.Edge != first.Start.Edge ||
            candidate.Start.WallDeviceX != first.Start.WallDeviceX ||
            Math.Abs(candidate.Start.DpiScaleX - first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(candidate.Start.DpiScaleY - first.Start.DpiScaleY) > 0.001);
        if (!string.IsNullOrEmpty(mismatchedGeometry.PaperId))
        {
            return Reject(
                queueKey,
                "queue-geometry-mismatch",
                candidates,
                mismatchedGeometry);
        }

        var envelope = default(DeviceScreenRect);
        foreach (var member in changed)
        {
            envelope = Union(envelope, member.Start.Bounds);
            envelope = Union(envelope, member.Target.Bounds);
        }
        if (envelope.IsEmpty)
        {
            return Reject(queueKey, "empty-envelope", candidates);
        }

#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.admission outcome=accepted queue={queueKey} " +
            $"candidates={candidates.Count} changed={changed.Length} " +
            $"roles={string.Join(',', changed.Select(member => $"{EdgeCapsulePerformanceDiagnostics.ShortId(member.PaperId)}:{member.Role}"))} " +
            $"durationMs={changedCandidates.Max(candidate => candidate.Motion.DurationMilliseconds)}");
#endif
        return new EdgeCapsuleQueueProxyPlan(
            queueKey,
            envelope,
            first.Start.Edge,
            first.Start.WallDeviceX,
            first.Start.DpiScaleX,
            first.Start.DpiScaleY,
            Math.Max(
                1,
                changedCandidates.Max(candidate => candidate.Motion.DurationMilliseconds)),
            Topmost: true,
            changed);
    }

    private static string? ChangedCandidateRejection(
        EdgeCapsuleQueueProxyCandidate candidate,
        string queueKey)
    {
        if (!candidate.Topmost)
        {
            return "changed-member-not-topmost";
        }
        if (!candidate.HostReady)
        {
            return "changed-member-host-not-ready";
        }
        if (!candidate.Start.IsUsable)
        {
            return "changed-member-start-unusable";
        }
        if (!candidate.Target.IsUsable)
        {
            return "changed-member-target-unusable";
        }
        if (!candidate.Start.Visible || !candidate.Target.Visible)
        {
            return "changed-member-hidden";
        }
        if (candidate.Start.Bounds.IsEmpty || candidate.Target.Bounds.IsEmpty)
        {
            return "changed-member-empty-bounds";
        }
        if (candidate.Motion.Kind != EdgeCapsuleMotionKind.Animate)
        {
            return $"changed-member-motion-{candidate.Motion.Kind}";
        }
        if (candidate.Start.Edge != candidate.Target.Edge)
        {
            return "changed-member-edge-change";
        }
        if (candidate.Start.WallDeviceX != candidate.Target.WallDeviceX)
        {
            return "changed-member-wall-change";
        }
        if (Math.Abs(candidate.Start.DpiScaleX - candidate.Target.DpiScaleX) > 0.001 ||
            Math.Abs(candidate.Start.DpiScaleY - candidate.Target.DpiScaleY) > 0.001)
        {
            return "changed-member-dpi-change";
        }
        if (!string.Equals(candidate.QueueKey, queueKey, StringComparison.Ordinal))
        {
            return "changed-member-queue-change";
        }
        return null;
    }

    private static EdgeCapsuleQueueProxyPlan? Reject(
        string queueKey,
        string reason,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates,
        EdgeCapsuleQueueProxyCandidate? offending = null)
    {
#if DEBUG
        var changedCount = candidates.Count(candidate =>
            !FramesVisuallyMatch(candidate.Start, candidate.Target));
        var detail = offending is { } candidate
            ? $" paper={EdgeCapsulePerformanceDiagnostics.ShortId(candidate.PaperId)} " +
              $"motion={candidate.Motion.Kind}/{candidate.Motion.Reason} " +
              $"hostReady={candidate.HostReady} topmost={candidate.Topmost} " +
              $"start={candidate.Start.Surface}:{candidate.Start.Bounds.Left},{candidate.Start.Bounds.Top}," +
              $"{candidate.Start.Bounds.Width}x{candidate.Start.Bounds.Height} " +
              $"target={candidate.Target.Surface}:{candidate.Target.Bounds.Left},{candidate.Target.Bounds.Top}," +
              $"{candidate.Target.Bounds.Width}x{candidate.Target.Bounds.Height} " +
              $"wall={candidate.Start.WallDeviceX}->{candidate.Target.WallDeviceX} " +
              $"dpi={candidate.Start.DpiScaleX:F3},{candidate.Start.DpiScaleY:F3}->" +
              $"{candidate.Target.DpiScaleX:F3},{candidate.Target.DpiScaleY:F3}"
            : string.Empty;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.admission outcome=rejected queue={queueKey} reason={reason} " +
            $"candidates={candidates.Count} changed={changedCount}{detail}");
#endif
        return null;
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
        EdgeCapsuleQueueProxyCandidate candidate)
    {
        var start = candidate.Start;
        var target = candidate.Target;
        if (start.Surface != EdgeCapsuleSurfaceKind.DockedPreview &&
            target.Surface == EdgeCapsuleSurfaceKind.DockedPreview)
        {
            return EdgeCapsuleQueueProxyMemberRole.SnapshotMorph;
        }
        if (start.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
            target.Surface != EdgeCapsuleSurfaceKind.DockedPreview)
        {
            return EdgeCapsuleQueueProxyMemberRole.ClosingPreview;
        }
        if (candidate.Motion.Reason == EdgeCapsuleTransitionReason.Pointer &&
            !CanWrapMovingMemberLive(start, target))
        {
            return EdgeCapsuleQueueProxyMemberRole.SnapshotMorph;
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
}