namespace PaperTodo;

internal readonly record struct EdgeCapsuleLayoutFacts(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    EdgeCapsulePlacement Placement,
    double QueueStartTopMarginDip,
    double GapDip,
    double RestingWidthDip,
    double MaximumCloseWidthDip,
    double HostWidthDip,
    double HostHeightDip,
    double HeightDip,
    double PreviewWidthDip,
    double PreviewHeightDip,
    double MaximumPreviewHeightDip,
    bool UsesFixedMotionHost,
    bool CloseSegmentActsAsContent,
    double RestingContentOpacity,
    double? ForcedContentOpacity);

/// <summary>
/// Converts measured/environment facts into the planner snapshot. PaperWindow supplies target
/// monitor and text width only; queue top policy lives here and is independently testable.
/// </summary>
internal static class EdgeCapsuleLayoutService
{
    public static EdgeCapsuleLayoutSnapshot Calculate(EdgeCapsuleLayoutFacts facts)
    {
        var placement = facts.Placement.Normalize();
        var localWorkArea = facts.Monitor.LocalWorkAreaDip;
        var normalTop = placement.IsPlaced
            ? EdgeCapsuleLayout.TopForIndex(
                placement.VisualIndex,
                facts.QueueStartTopMarginDip,
                localWorkArea,
                placement.SlotCount,
                facts.GapDip) +
              placement.TopOffsetDip
            : 0;
        var masterTop = placement.IsPlaced
            ? EdgeCapsuleLayout.TopForIndex(
                0,
                facts.QueueStartTopMarginDip,
                localWorkArea,
                placement.SlotCount,
                facts.GapDip)
            : normalTop;
        var queueTop = placement.IsPlaced
            ? EdgeCapsuleLayout.TopForIndex(
                0,
                facts.QueueStartTopMarginDip,
                localWorkArea,
                placement.SlotCount,
                facts.GapDip)
            : normalTop;
        var lastSlotTop = placement.IsPlaced
            ? EdgeCapsuleLayout.TopForIndex(
                Math.Max(0, placement.SlotCount - 1),
                facts.QueueStartTopMarginDip,
                localWorkArea,
                placement.SlotCount,
                facts.GapDip)
            : normalTop;
        var hostEnvelope = facts.UsesFixedMotionHost
            ? EdgeCapsuleMotionEnvelopePolicy.CalculateQueueEnvelope(
                localWorkArea.Top,
                localWorkArea.Bottom,
                queueTop,
                lastSlotTop,
                normalTop,
                facts.HostHeightDip,
                facts.MaximumPreviewHeightDip,
                Math.Max(facts.HeightDip, facts.PreviewHeightDip))
            : new EdgeCapsuleHostEnvelopeLayout(
                normalTop,
                Math.Max(1, facts.HostHeightDip));
        return new EdgeCapsuleLayoutSnapshot(
            facts.Monitor,
            facts.Edge,
            normalTop,
            masterTop,
            facts.RestingWidthDip,
            facts.MaximumCloseWidthDip,
            facts.HostWidthDip,
            facts.HostHeightDip,
            facts.HeightDip,
            facts.PreviewWidthDip,
            facts.PreviewHeightDip,
            hostEnvelope.TopDip,
            hostEnvelope.HeightDip,
            facts.UsesFixedMotionHost,
            facts.CloseSegmentActsAsContent,
            facts.RestingContentOpacity,
            facts.ForcedContentOpacity);
    }

    public static double TopForVisualIndex(
        MonitorGeometry monitor,
        int visualIndex,
        int slotCount,
        double queueStartTopMarginDip,
        double gapDip) =>
        EdgeCapsuleLayout.TopForIndex(
            visualIndex,
            queueStartTopMarginDip,
            monitor.LocalWorkAreaDip,
            slotCount,
            gapDip);
}
