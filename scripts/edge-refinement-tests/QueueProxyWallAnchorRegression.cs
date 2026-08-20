extern alias PaperTodoApp;

using AppEdge =
    PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppFrame =
    PaperTodoApp::PaperTodo.EdgeCapsulePresentationFrame;
using AppGeometry =
    PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyPolicy;
using AppRect =
    PaperTodoApp::PaperTodo.DeviceScreenRect;
using AppSurface =
    PaperTodoApp::PaperTodo.EdgeCapsuleSurfaceKind;

namespace PaperTodo;

internal static class QueueProxyWallAnchorRegression
{
    public static void Run()
    {
        CheckWall(AppEdge.Right, 5120);
        CheckWall(AppEdge.Left, -2560);
    }

    private static void CheckWall(
        AppEdge edge,
        int wall)
    {
        var bounds = edge == AppEdge.Right
            ? new AppRect(wall - 86, 100, wall, 158)
            : new AppRect(wall, 100, wall + 86, 158);
        var host = edge == AppEdge.Right
            ? new AppRect(wall - 260, 100, wall, 280)
            : new AppRect(wall, 100, wall + 260, 280);
        var frame = new AppFrame(
            true,
            AppSurface.DockedResting,
            bounds,
            host,
            bounds,
            edge,
            68,
            wall,
            1,
            1,
            18,
            1,
            1,
            false,
            true,
            false);
        var presented =
            AppGeometry.PresentedHostBounds(frame);
        Assert(
            edge == AppEdge.Right
                ? presented.Right == wall
                : presented.Left == wall,
            $"{edge} host moved away from wall");
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
