namespace PaperTodo;

internal readonly record struct EdgeCapsuleProxyClipRect(
    float Left,
    float Top,
    float Right,
    float Bottom)
{
    public float Width => Math.Max(0, Right - Left);
    public float Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Pure physical-pixel geometry for the queue compositor. A morph reveals or conceals a
/// native-resolution HWND surface with a rectangle clip; no bitmap or live HWND surface is scaled
/// to simulate another window size.
/// </summary>
internal static class EdgeCapsuleQueueProxyGeometry
{
    internal const int OutputOverscanPixels = 4;

    internal static DeviceScreenRect OutputBounds(DeviceScreenRect envelope)
    {
        if (envelope.IsEmpty)
        {
            return default;
        }

        return new DeviceScreenRect(
            envelope.Left - OutputOverscanPixels,
            envelope.Top - OutputOverscanPixels,
            envelope.Right + OutputOverscanPixels,
            envelope.Bottom + OutputOverscanPixels);
    }

    internal static bool Contains(
        DeviceScreenRect outer,
        DeviceScreenRect inner) =>
        !outer.IsEmpty &&
        !inner.IsEmpty &&
        inner.Left >= outer.Left &&
        inner.Top >= outer.Top &&
        inner.Right <= outer.Right &&
        inner.Bottom <= outer.Bottom;

    internal static DeviceScreenRect Union(
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

    internal static DeviceScreenRect WithDownwardCapacity(
        DeviceScreenRect bounds,
        int downwardShiftDevice,
        int workAreaBottomDevice)
    {
        if (bounds.IsEmpty || downwardShiftDevice <= 0)
        {
            return bounds;
        }

        var requestedBottom = Math.Min(
            int.MaxValue,
            (long)bounds.Bottom + downwardShiftDevice);
        return new DeviceScreenRect(
            bounds.Left,
            bounds.Top,
            bounds.Right,
            (int)Math.Min(
                requestedBottom,
                workAreaBottomDevice));
    }

    internal static EdgeCapsuleProxyClipRect ClipForVisibleBounds(
        DeviceScreenRect sourceBounds,
        DeviceScreenRect visibleBounds)
    {
        if (sourceBounds.IsEmpty || visibleBounds.IsEmpty)
        {
            return default;
        }

        var left = Math.Clamp(
            visibleBounds.Left - sourceBounds.Left,
            0,
            sourceBounds.Width);
        var top = Math.Clamp(
            visibleBounds.Top - sourceBounds.Top,
            0,
            sourceBounds.Height);
        var right = Math.Clamp(
            visibleBounds.Right - sourceBounds.Left,
            0,
            sourceBounds.Width);
        var bottom = Math.Clamp(
            visibleBounds.Bottom - sourceBounds.Top,
            0,
            sourceBounds.Height);
        return new EdgeCapsuleProxyClipRect(left, top, right, bottom);
    }

    internal static EdgeCapsuleProxyClipRect FullClip(
        DeviceScreenRect sourceBounds) =>
        sourceBounds.IsEmpty
            ? default
            : new EdgeCapsuleProxyClipRect(
                0,
                0,
                sourceBounds.Width,
                sourceBounds.Height);

    /// <summary>
    /// Returns the clip that owns a newly-created moving capsule edge. A WPF edge HWND includes a
    /// transparent shadow margin above, below and on the screen-internal side. Keep every animation
    /// endpoint on the focus-outline silhouette: transitioning the clip to the outer HWND while
    /// holding a fixed radius intersects two offset arcs and makes the visible corner shrink.
    /// </summary>
    internal static EdgeCapsuleProxyClipRect RoundedBodyClipForVisibleBounds(
        DeviceScreenRect sourceBounds,
        DeviceScreenRect visibleBounds,
        EdgeCapsuleEdge edge,
        double dpiScaleX,
        double dpiScaleY)
    {
        var clip = ClipForVisibleBounds(
            sourceBounds,
            visibleBounds);
        if (clip.IsEmpty)
        {
            return clip;
        }

        var marginX = (float)(
            EdgeCapsuleLayout.OutlineSilhouetteInset *
            Math.Max(1, dpiScaleX));
        var marginY = (float)(
            EdgeCapsuleLayout.OutlineSilhouetteInset *
            Math.Max(1, dpiScaleY));
        var left = clip.Left;
        var right = clip.Right;
        if (edge == EdgeCapsuleEdge.Right)
        {
            left = Math.Min(right, left + marginX);
        }
        else
        {
            right = Math.Max(left, right - marginX);
        }

        var top = Math.Min(clip.Bottom, clip.Top + marginY);
        var bottom = Math.Max(top, clip.Bottom - marginY);
        return new EdgeCapsuleProxyClipRect(
            left,
            top,
            right,
            bottom);
    }

    internal static float RoundedBodyClipRadius(
        double dpiScale,
        float clipSpan) =>
        clipSpan <= 0
            ? 0
            : (float)Math.Min(
                EdgeCapsuleLayout.OutlineSilhouetteRadius *
                    Math.Max(1, dpiScale),
                clipSpan / 2.0);

    internal static EdgeCapsuleProxyClipRect Interpolate(
        EdgeCapsuleProxyClipRect start,
        EdgeCapsuleProxyClipRect target,
        double progress) => new(
        Lerp(start.Left, target.Left, progress),
        Lerp(start.Top, target.Top, progress),
        Lerp(start.Right, target.Right, progress),
        Lerp(start.Bottom, target.Bottom, progress));

    private static float Lerp(float from, float to, double progress) =>
        (float)(from + (to - from) * progress);
}
