extern alias PaperTodoApp;

using System.Runtime.CompilerServices;
using AppClip =
    PaperTodoApp::PaperTodo.EdgeCapsuleProxyClipRect;
using AppEdge =
    PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppGeometry =
    PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyGeometry;
using AppLayout =
    PaperTodoApp::PaperTodo.EdgeCapsuleLayout;
using AppRect =
    PaperTodoApp::PaperTodo.DeviceScreenRect;

namespace PaperTodo;

internal static class QueueProxyNativeClipRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var target = new AppRect(4745, 185, 5120, 423);
        var compact = new AppRect(5026, 185, 5120, 243);
        var revealStart = AppGeometry.RoundedBodyClipForVisibleBounds(
            target,
            compact,
            AppEdge.Right,
            dpiScaleX: 1,
            dpiScaleY: 1);
        AssertClip(
            revealStart,
            left: 288,
            top: 7,
            right: 375,
            bottom: 51,
            "right-wall reveal start");
        Assert(
            AppGeometry.FullClip(target) ==
                new AppClip(0, 0, 375, 238),
            "full target clip changed");
        var revealTarget =
            AppGeometry.RoundedBodyClipForVisibleBounds(
                target,
                target,
                AppEdge.Right,
                dpiScaleX: 1,
                dpiScaleY: 1);
        AssertClip(
            revealTarget,
            left: 7,
            top: 7,
            right: 375,
            bottom: 231,
            "right-wall reveal endpoint");
        Assert(
            revealTarget != AppGeometry.FullClip(target),
            "the endpoint clip must stay on the outline silhouette, not the transparent HWND");

        var radiusX =
            AppGeometry.RoundedBodyClipRadius(
                dpiScale: 1,
                revealStart.Width);
        var radiusY =
            AppGeometry.RoundedBodyClipRadius(
                dpiScale: 1,
                revealStart.Height);
        Assert(
            Math.Abs(radiusX - (AppLayout.CornerRadius + 1)) < 0.001,
            "rounded clip must retain the outer outline horizontal radius");
        Assert(
            Math.Abs(radiusY - (AppLayout.CornerRadius + 1)) < 0.001 &&
            Math.Abs(radiusX - radiusY) < 0.001,
            "rounded clip must retain one circular outer outline radius");

        var intermediate = new AppRect(4920, 185, 5120, 325);
        var concealStart = AppGeometry.RoundedBodyClipForVisibleBounds(
            target,
            intermediate,
            AppEdge.Right,
            dpiScaleX: 1,
            dpiScaleY: 1);
        var concealEnd = AppGeometry.RoundedBodyClipForVisibleBounds(
            target,
            compact,
            AppEdge.Right,
            dpiScaleX: 1,
            dpiScaleY: 1);
        AssertClip(
            concealStart,
            left: 182,
            top: 7,
            right: 375,
            bottom: 133,
            "mid-flight conceal start");
        AssertClip(
            concealEnd,
            left: 288,
            top: 7,
            right: 375,
            bottom: 51,
            "conceal endpoint");
        Assert(
            revealStart.Right == target.Width &&
            concealEnd.Right == target.Width,
            "right-wall clip must keep the wall-side edge fixed");

        var leftTarget = new AppRect(0, 185, 375, 423);
        var leftCompact = new AppRect(0, 185, 94, 243);
        var leftReveal = AppGeometry.RoundedBodyClipForVisibleBounds(
            leftTarget,
            leftCompact,
            AppEdge.Left,
            dpiScaleX: 1,
            dpiScaleY: 1);
        AssertClip(
            leftReveal,
            left: 0,
            top: 7,
            right: 87,
            bottom: 51,
            "left-wall reveal start");
        Assert(leftReveal.Left == 0,
            "left-wall clip must keep the wall-side edge fixed");

        Assert(AppGeometry.Contains(target, compact),
            "target must contain compact reveal rectangle");
        Assert(!AppGeometry.Contains(compact, target),
            "compact rectangle cannot contain target");

        var output = AppGeometry.OutputBounds(target);
        Assert(
            output.Left < target.Left &&
            output.Top < target.Top &&
            output.Right > target.Right &&
            output.Bottom > target.Bottom,
            "native compositor output must overscan every edge");

        var queueEnvelope = new AppRect(4745, 185, 5120, 780);
        var queueCapacity = AppGeometry.WithDownwardCapacity(
            queueEnvelope,
            downwardShiftDevice: 180,
            workAreaBottomDevice: 900);
        Assert(
            queueCapacity == new AppRect(4745, 185, 5120, 900),
            "queue capacity must reserve the largest future preview displacement without exceeding the work area");
    }

    private static void AssertClip(
        AppClip actual,
        float left,
        float top,
        float right,
        float bottom,
        string message)
    {
        Assert(
            Math.Abs(actual.Left - left) < 0.001 &&
            Math.Abs(actual.Top - top) < 0.001 &&
            Math.Abs(actual.Right - right) < 0.001 &&
            Math.Abs(actual.Bottom - bottom) < 0.001,
            $"{message}: got {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
