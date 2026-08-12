namespace PaperTodo;

internal readonly record struct EdgeCapsuleHostEnvelopeLayout(
    double TopDip,
    double HeightDip)
{
    public bool IsUsable =>
        double.IsFinite(TopDip) &&
        double.IsFinite(HeightDip) &&
        HeightDip > 0;
}

/// <summary>
/// Pure geometry for the fixed native host used by edge-motion V2. The HWND reserves the queue's
/// complete vertical motion range once; individual capsules then move only their WPF surface.
/// </summary>
internal static class EdgeCapsuleMotionEnvelopePolicy
{
    private const string EnabledEnvironmentVariable = "PAPERTODO_EDGE_MOTION_V2";

    /// <summary>
    /// V2 is the production path. The environment override exists only for same-binary A/B traces.
    /// </summary>
    public static bool IsEnabled { get; } = ReadEnabledOverride();

    public static EdgeCapsuleHostEnvelopeLayout CalculateQueueEnvelope(
        double workAreaTopDip,
        double workAreaBottomDip,
        double queueTopDip,
        double lastSlotTopDip,
        double currentTopDip,
        double hostCapacityHeightDip,
        double maximumPreviewHeightDip,
        double currentVisibleHeightDip)
    {
        var capacityHeight = Math.Max(
            Math.Max(1, hostCapacityHeightDip),
            Math.Max(1, maximumPreviewHeightDip));
        var top = Math.Min(
            workAreaTopDip,
            Math.Min(queueTopDip, currentTopDip));
        // A preview pushes a following compact slot by exactly previewHeight - compactHeight, so
        // that slot's visible bottom never exceeds its base top + maximum preview height. Adding
        // both the displacement and full capacity here would reserve the same expansion twice.
        var bottom = Math.Max(
            workAreaBottomDip,
            Math.Max(
                currentTopDip + Math.Max(1, currentVisibleHeightDip),
                lastSlotTopDip + capacityHeight));
        return new EdgeCapsuleHostEnvelopeLayout(
            top,
            Math.Max(1, bottom - top));
    }

    public static DeviceScreenRect UnionWallPinned(
        DeviceScreenRect first,
        DeviceScreenRect second,
        EdgeCapsuleEdge edge,
        int wallDeviceX)
    {
        if (first.IsEmpty)
        {
            return second;
        }
        if (second.IsEmpty)
        {
            return first;
        }

        var width = Math.Max(first.Width, second.Width);
        var left = edge == EdgeCapsuleEdge.Left
            ? wallDeviceX
            : wallDeviceX - width;
        var top = Math.Min(first.Top, second.Top);
        var bottom = Math.Max(first.Bottom, second.Bottom);
        return new DeviceScreenRect(left, top, left + width, bottom);
    }

    public static bool Contains(
        DeviceScreenRect envelope,
        DeviceScreenRect bounds) =>
        !envelope.IsEmpty &&
        !bounds.IsEmpty &&
        envelope.Left <= bounds.Left &&
        envelope.Top <= bounds.Top &&
        envelope.Right >= bounds.Right &&
        envelope.Bottom >= bounds.Bottom;

    private static bool ReadEnabledOverride()
    {
        var value = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)
            ?.Trim()
            .ToLowerInvariant();
        return value is not ("0" or "false" or "off" or "none");
    }
}
