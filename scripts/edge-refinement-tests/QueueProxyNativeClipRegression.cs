extern alias PaperTodoApp;

using System.Runtime.CompilerServices;
using AppClip =
    PaperTodoApp::PaperTodo.EdgeCapsuleProxyClipRect;
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
        var revealStart = AppGeometry.ClipForVisibleBounds(
            target,
            compact);
        AssertClip(
            revealStart,
            left: 281,
            top: 0,
            right: 375,
            bottom: 58,
            "right-wall reveal start");
        Assert(
            AppGeometry.FullClip(target) ==
                new AppClip(0, 0, 375, 238),
            "full target clip changed");

        var compensatedRadiusX =
            AppGeometry.OuterClipRadiusForBodyCorner(
                dpiScale: 1,
                revealStart.Width);
        var compensatedRadiusY =
            AppGeometry.OuterClipRadiusForBodyCorner(
                dpiScale: 1,
                revealStart.Height);
        var expectedUnclampedRadius =
            AppLayout.CornerRadius +
            AppLayout.WindowChromeMargin +
            Math.Sqrt(
                2 *
                AppLayout.CornerRadius *
                AppLayout.WindowChromeMargin);
        Assert(
            Math.Abs(compensatedRadiusX - expectedUnclampedRadius) < 0.001,
            "wide reveal clip must compensate the transparent chrome margin");
        Assert(
            Math.Abs(
                compensatedRadiusY -
                revealStart.Height / 2) < 0.001,
            "compact reveal clip radius must stay within its height");

        var bodyTop = (float)AppLayout.WindowChromeMargin;
        var normalizedY =
            (compensatedRadiusY - bodyTop) /
            compensatedRadiusY;
        var visibleBodyInset =
            compensatedRadiusX *
            (1 - Math.Sqrt(
                Math.Max(
                    0,
                    1 - normalizedY * normalizedY)));
        Assert(
            visibleBodyInset >=
                AppLayout.CornerRadius * 0.75,
            "outer clip rounding must remain visible at the opaque body edge");

        var intermediate = new AppRect(4920, 185, 5120, 325);
        var concealStart = AppGeometry.ClipForVisibleBounds(
            target,
            intermediate);
        var concealEnd = AppGeometry.ClipForVisibleBounds(
            target,
            compact);
        AssertClip(
            concealStart,
            left: 175,
            top: 0,
            right: 375,
            bottom: 140,
            "mid-flight conceal start");
        AssertClip(
            concealEnd,
            left: 281,
            top: 0,
            right: 375,
            bottom: 58,
            "conceal endpoint");
        Assert(
            revealStart.Right == target.Width &&
            concealEnd.Right == target.Width,
            "right-wall clip must keep the wall-side edge fixed");

        var leftTarget = new AppRect(0, 185, 375, 423);
        var leftCompact = new AppRect(0, 185, 94, 243);
        var leftReveal = AppGeometry.ClipForVisibleBounds(
            leftTarget,
            leftCompact);
        AssertClip(
            leftReveal,
            left: 0,
            top: 0,
            right: 94,
            bottom: 58,
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
