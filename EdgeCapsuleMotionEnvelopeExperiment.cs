namespace PaperTodo;

/// <summary>
/// Debug-only architecture experiment for right-edge queues. Each host reserves maximum preview
/// capacity, then a moving capsule keeps one native rectangle large enough for both endpoints while
/// its WPF surface moves inside that rectangle.
/// Set PAPERTODO_EDGE_MOTION_ENVELOPE=0 to disable it in the same build, or use left/all to select
/// a different edge. Release builds retain the production HWND-per-frame path.
/// </summary>
internal static class EdgeCapsuleMotionEnvelopeExperiment
{
#if DEBUG
    private static readonly string Mode =
        Environment.GetEnvironmentVariable("PAPERTODO_EDGE_MOTION_ENVELOPE")?.Trim()
            .ToLowerInvariant() ?? "right";
#endif

    public static bool IsEnabledForEdge(EdgeCapsuleEdge edge)
    {
#if DEBUG
        return Mode switch
        {
            "0" or "false" or "off" or "none" => false,
            "left" => edge == EdgeCapsuleEdge.Left,
            "all" or "both" => true,
            _ => edge == EdgeCapsuleEdge.Right
        };
#else
        return false;
#endif
    }

    public static bool ShouldUse(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target) =>
        IsEnabledForEdge(target.Edge) &&
        (applied.UsesMotionEnvelope || applied.Bounds.Top != target.Bounds.Top);

    public static DeviceScreenRect CreateVerticalEnvelope(
        DeviceScreenRect startNativeBounds,
        DeviceScreenRect targetHostBounds,
        EdgeCapsuleEdge edge,
        int wallDeviceX)
    {
        if (startNativeBounds.IsEmpty || targetHostBounds.IsEmpty)
        {
            return default;
        }

        var width = Math.Max(startNativeBounds.Width, targetHostBounds.Width);
        var left = edge == EdgeCapsuleEdge.Left
            ? wallDeviceX
            : wallDeviceX - width;
        var top = Math.Min(startNativeBounds.Top, targetHostBounds.Top);
        var bottom = Math.Max(startNativeBounds.Bottom, targetHostBounds.Bottom);
        return new DeviceScreenRect(left, top, left + width, bottom);
    }
}
