namespace PaperTodo;

internal readonly record struct EdgeCapsulePreviewCorridorNode(
    DeviceScreenRect Bounds,
    bool ConnectToPrevious);

internal static class EdgeCapsulePreviewCorridor
{
    public static bool Contains(
        ReadOnlySpan<EdgeCapsulePreviewCorridorNode> nodes,
        DeviceScreenPoint pointer,
        int horizontalTolerance,
        int verticalTolerance)
    {
        if (nodes.IsEmpty)
        {
            return false;
        }

        var horizontal = Math.Max(0, horizontalTolerance);
        var vertical = Math.Max(0, verticalTolerance);
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (ContainsInflated(
                    node.Bounds,
                    pointer,
                    horizontal,
                    vertical))
            {
                return true;
            }
            if (index > 0 &&
                node.ConnectToPrevious &&
                ContainsBridge(
                    nodes[index - 1].Bounds,
                    node.Bounds,
                    pointer,
                    horizontal,
                    vertical))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsInflated(
        DeviceScreenRect bounds,
        DeviceScreenPoint pointer,
        int horizontalTolerance,
        int verticalTolerance) =>
        !bounds.IsEmpty &&
        pointer.X >= bounds.Left - (double)horizontalTolerance &&
        pointer.X < bounds.Right + (double)horizontalTolerance &&
        pointer.Y >= bounds.Top - (double)verticalTolerance &&
        pointer.Y < bounds.Bottom + (double)verticalTolerance;

    private static bool ContainsBridge(
        DeviceScreenRect first,
        DeviceScreenRect second,
        DeviceScreenPoint pointer,
        int horizontalTolerance,
        int verticalTolerance)
    {
        if (first.IsEmpty || second.IsEmpty)
        {
            return false;
        }

        var upper = first.Top <= second.Top ? first : second;
        var lower = first.Top <= second.Top ? second : first;
        var bridgeLeft = Math.Max(upper.Left, lower.Left) -
            (double)horizontalTolerance;
        var bridgeRight = Math.Min(upper.Right, lower.Right) +
            (double)horizontalTolerance;
        if (bridgeRight <= bridgeLeft)
        {
            return false;
        }

        var bridgeTop = upper.Bottom - (double)verticalTolerance;
        var bridgeBottom = lower.Top + (double)verticalTolerance;
        return bridgeBottom > bridgeTop &&
            pointer.X >= bridgeLeft &&
            pointer.X < bridgeRight &&
            pointer.Y >= bridgeTop &&
            pointer.Y < bridgeBottom;
    }
}
