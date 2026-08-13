extern alias PaperTodoApp;

using System.Runtime.CompilerServices;
using AppEdge = PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppGeometry = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyGeometry;
using AppRect = PaperTodoApp::PaperTodo.DeviceScreenRect;

namespace PaperTodo;

internal static class QueueProxyWallAnchorRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var envelope = new AppRect(100, 100, 500, 300);
        var output = AppGeometry.OutputBounds(envelope);
        Assert(output.Left < envelope.Left && output.Top < envelope.Top &&
               output.Right > envelope.Right && output.Bottom > envelope.Bottom,
            "queue proxy output must overscan every animation edge");

        CheckWallAnchor(AppEdge.Right, 500, 80, output);
        CheckWallAnchor(AppEdge.Right, 500, 320, output);
        CheckWallAnchor(AppEdge.Left, 100, 80, output);
        CheckWallAnchor(AppEdge.Left, 100, 320, output);
    }

    private static void CheckWallAnchor(
        AppEdge edge,
        int wall,
        int sourceWidth,
        AppRect output)
    {
        var offset = AppGeometry.WallPinnedOffsetX(edge, wall, sourceWidth, output);
        foreach (var scale in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
        {
            var presentedWall = AppGeometry.PresentedWallDeviceX(
                edge,
                output.Left,
                offset,
                sourceWidth,
                scale);
            Assert(Math.Abs(presentedWall - wall) < 0.001,
                $"{edge} wall moved at scale {scale}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
