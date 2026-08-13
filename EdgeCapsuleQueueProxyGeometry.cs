namespace PaperTodo;

/// <summary>
/// Pure physical-pixel geometry for the queue compositor. The output HWND deliberately owns a
/// small transparent overscan so DComp filtering and the WPF shadow never meet an exact window
/// boundary. Horizontal scaling is anchored directly at the monitor wall; no independent X
/// translation animation is required to keep a resizing capsule attached to the edge.
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

    internal static float ScaleCenterX(EdgeCapsuleEdge edge, int sourceWidth) =>
        edge == EdgeCapsuleEdge.Left ? 0 : Math.Max(1, sourceWidth);

    internal static float WallPinnedOffsetX(
        EdgeCapsuleEdge edge,
        int wallDeviceX,
        int sourceWidth,
        DeviceScreenRect outputBounds) =>
        edge == EdgeCapsuleEdge.Left
            ? wallDeviceX - outputBounds.Left
            : wallDeviceX - outputBounds.Left - Math.Max(1, sourceWidth);

    internal static double PresentedWallDeviceX(
        EdgeCapsuleEdge edge,
        double outputLeft,
        double offsetX,
        double sourceWidth,
        double scaleX)
    {
        var centerX = edge == EdgeCapsuleEdge.Left ? 0 : sourceWidth;
        var left = outputLeft + offsetX + centerX * (1 - scaleX);
        var right = left + sourceWidth * scaleX;
        return edge == EdgeCapsuleEdge.Left ? left : right;
    }
}
