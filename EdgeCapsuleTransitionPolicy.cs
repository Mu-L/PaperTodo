namespace PaperTodo;

/// <summary>
/// Pure transition policy. It retargets from the last applied frame; pointer intent is never
/// locked behind an obsolete transition.
/// </summary>
internal static class EdgeCapsuleTransitionPolicy
{
    public static EdgeCapsuleTransition? Create(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target,
        EdgeCapsuleMotion motion,
        bool transitionAlreadyActive,
        long nowTimestamp,
        long timestampFrequency)
    {
        if (!target.Visible ||
            !applied.Visible ||
            applied.Bounds.IsEmpty ||
            applied.HostBounds.IsEmpty ||
            motion.Kind == EdgeCapsuleMotionKind.Snap ||
            (motion.Kind == EdgeCapsuleMotionKind.Preserve && !transitionAlreadyActive) ||
            applied.Edge != target.Edge ||
            applied.WallDeviceX != target.WallDeviceX ||
            Math.Abs(applied.DpiScaleX - target.DpiScaleX) > 0.001 ||
            Math.Abs(applied.DpiScaleY - target.DpiScaleY) > 0.001)
        {
            return null;
        }

        if (FramesMatch(applied, target))
        {
            return null;
        }

        var durationMilliseconds = motion.Kind == EdgeCapsuleMotionKind.Preserve
            ? EdgeCapsuleLayout.SlotMoveMilliseconds
            : Math.Max(1, motion.DurationMilliseconds);
        var durationTicks = Math.Max(
            1,
            (long)Math.Round(timestampFrequency * durationMilliseconds / 1000.0));
        var transitionHostBounds = target.UsesFixedMotionHost
            ? EdgeCapsuleMotionEnvelopePolicy.UnionWallPinned(
                applied.HostBounds,
                target.HostBounds,
                target.Edge,
                target.WallDeviceX)
            : default;
        return new EdgeCapsuleTransition(
            applied,
            target,
            nowTimestamp,
            durationTicks,
            motion.Reason,
            transitionHostBounds);
    }

    public static EdgeCapsuleTransitionSample Sample(
        EdgeCapsuleTransition transition,
        long nowTimestamp)
    {
        var elapsed = Math.Max(0, nowTimestamp - transition.StartedAtTimestamp);
        var rawProgress = Math.Clamp(
            elapsed / (double)Math.Max(1, transition.DurationTimestampTicks),
            0,
            1);
        if (rawProgress >= 1)
        {
            var completeFrame = transition.Target.ToFrame();
            if (transition.Target.UsesFixedMotionHost)
            {
                completeFrame = completeFrame with
                {
                    HostBounds = transition.HostBounds
                };
            }
            return new EdgeCapsuleTransitionSample(
                completeFrame,
                true);
        }

        var progress = EaseOutCubic(rawProgress);
        var target = transition.Target;
        var start = transition.Start;
        var width = LerpDevice(start.Bounds.Width, target.Bounds.Width, progress);
        var height = LerpDevice(start.Bounds.Height, target.Bounds.Height, progress);
        var top = LerpDevice(start.Bounds.Top, target.Bounds.Top, progress);
        var left = target.Edge == EdgeCapsuleEdge.Left
            ? target.WallDeviceX
            : target.WallDeviceX - width;
        var right = target.Edge == EdgeCapsuleEdge.Left
            ? target.WallDeviceX + width
            : target.WallDeviceX;
        var bounds = new DeviceScreenRect(left, top, right, top + height);
        DeviceScreenRect hostBounds;
        if (target.UsesFixedMotionHost)
        {
            hostBounds = transition.HostBounds;
        }
        else
        {
            // Legacy A/B path: native capacity follows the interpolated top exactly.
            var hostWidth = Math.Max(
                bounds.Width,
                Math.Max(start.HostBounds.Width, target.HostBounds.Width));
            var hostHeight = Math.Max(
                bounds.Height,
                Math.Max(start.HostBounds.Height, target.HostBounds.Height));
            hostBounds = EdgeCapsuleGeometry.HostBoundsForVisibleBounds(
                bounds,
                target.Edge,
                target.WallDeviceX,
                hostWidth,
                hostHeight);
        }
        // The old preview tree remains visible while its height shrinks, but it must stop owning
        // input as soon as the logical preview closes. Keep the outgoing surface identity through
        // retargets so a compact layout update cannot re-enable the tall card midway through.
        var outgoingPreview =
            start.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
            target.Surface != EdgeCapsuleSurfaceKind.DockedPreview;
        var hitTestVisible = target.IsHitTestVisible && !outgoingPreview;
        var interactiveBounds = hitTestVisible
            ? EdgeCapsuleGeometry.InteractiveBoundsForAppliedBounds(
                bounds,
                target.Edge,
                target.DpiScaleX,
                target.DpiScaleY,
                EdgeCapsuleLayout.WindowChromeMargin)
            : default;
        var frame = new EdgeCapsulePresentationFrame(
            true,
            outgoingPreview ? start.Surface : target.Surface,
            bounds,
            hostBounds,
            interactiveBounds,
            target.Edge,
            LerpDevice(start.BodyWindowWidthDevice, target.BodyWindowWidthDevice, progress),
            target.WallDeviceX,
            target.DpiScaleX,
            target.DpiScaleY,
            target.MaximumCloseWidthDip,
            Lerp(start.Opacity, target.Opacity, progress),
            Lerp(start.ContentOpacity, target.ContentOpacity, progress),
            target.OutlineVisible,
            hitTestVisible,
            target.CloseSegmentActsAsContent,
            target.UsesFixedMotionHost);
        return new EdgeCapsuleTransitionSample(frame, false);
    }

    public static EdgeCapsulePresentationFrame ResolveSettledFrame(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target)
    {
        if (FramesMatch(applied, target))
        {
            return applied;
        }

        var resolved = target.ToFrame();
        if (CanRetainFixedHost(applied, target))
        {
            resolved = resolved with { HostBounds = applied.HostBounds };
        }
        return resolved;
    }

    public static bool FramesMatch(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target) =>
        applied.Visible == target.Visible &&
        applied.Surface == target.Surface &&
        applied.Bounds == target.Bounds &&
        HostMatches(applied, target) &&
        applied.InteractiveBounds == target.InteractiveBounds &&
        applied.Edge == target.Edge &&
        applied.BodyWindowWidthDevice == target.BodyWindowWidthDevice &&
        applied.WallDeviceX == target.WallDeviceX &&
        Math.Abs(applied.DpiScaleX - target.DpiScaleX) < 0.001 &&
        Math.Abs(applied.DpiScaleY - target.DpiScaleY) < 0.001 &&
        Math.Abs(applied.MaximumCloseWidthDip - target.MaximumCloseWidthDip) < 0.001 &&
        Math.Abs(applied.Opacity - target.Opacity) < 0.001 &&
        Math.Abs(applied.ContentOpacity - target.ContentOpacity) < 0.001 &&
        applied.OutlineVisible == target.OutlineVisible &&
        applied.IsHitTestVisible == target.IsHitTestVisible &&
        applied.CloseSegmentActsAsContent == target.CloseSegmentActsAsContent &&
        applied.UsesFixedMotionHost == target.UsesFixedMotionHost;

    private static bool HostMatches(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target) =>
        CanRetainFixedHost(applied, target) ||
        applied.HostBounds == target.HostBounds;

    private static bool CanRetainFixedHost(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target) =>
        applied.Visible &&
        target.Visible &&
        applied.UsesFixedMotionHost &&
        target.UsesFixedMotionHost &&
        applied.Edge == target.Edge &&
        applied.WallDeviceX == target.WallDeviceX &&
        Math.Abs(applied.DpiScaleX - target.DpiScaleX) < 0.001 &&
        Math.Abs(applied.DpiScaleY - target.DpiScaleY) < 0.001 &&
        EdgeCapsuleMotionEnvelopePolicy.Contains(
            applied.HostBounds,
            target.HostBounds);

    private static int LerpDevice(int from, int to, double progress) =>
        (int)Math.Round(Lerp(from, to, progress), MidpointRounding.AwayFromZero);

    private static double Lerp(double from, double to, double progress) =>
        from + (to - from) * progress;

    private static double EaseOutCubic(double progress) =>
        1.0 - Math.Pow(1.0 - progress, 3.0);
}
