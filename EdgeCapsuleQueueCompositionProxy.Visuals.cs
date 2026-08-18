using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private VisualState AddVisual(
        EdgeCapsuleQueueCompositionProxyMember member,
        IntPtr sourceHandle,
        DeviceScreenRect sourceBounds,
        DeviceScreenRect startVisualBounds,
        DeviceScreenRect targetVisualBounds,
        IDCompositionVisual? referenceVisual)
    {
        IUnknown? surface = null;
        IDCompositionVisual? visual = null;
        try
        {
            _device.CreateSurfaceFromHwnd(
                sourceHandle,
                out var createdSurface).CheckError();
            surface = createdSurface;

            _device.CreateVisual(
                out IDCompositionVisual2 createdVisual).CheckError();
            visual = createdVisual;
            visual.SetContent(surface).CheckError();
            visual.SetBitmapInterpolationMode(
                BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();

            var startOffsetX =
                startVisualBounds.Left - _outputBounds.Left;
            var startOffsetY =
                startVisualBounds.Top - _outputBounds.Top;
            var targetOffsetX =
                targetVisualBounds.Left - _outputBounds.Left;
            var targetOffsetY =
                targetVisualBounds.Top - _outputBounds.Top;
            visual.SetOffsetX(startOffsetX).CheckError();
            visual.SetOffsetY(startOffsetY).CheckError();

            _root.AddVisual(
                visual,
                insertAbove: true,
                referenceVisual!).CheckError();

            var state = new VisualState
            {
                Member = member,
                PresentedSourceHandle = sourceHandle,
                SourceBounds = sourceBounds,
                Surface = surface,
                Visual = visual,
                StartOffsetX = startOffsetX,
                StartOffsetY = startOffsetY,
                TargetOffsetX = targetOffsetX,
                TargetOffsetY = targetOffsetY
            };
            _visuals.Add(state);
            surface = null;
            visual = null;
            return state;
        }
        finally
        {
            visual?.Dispose();
            surface?.Dispose();
        }
    }

    private void RebaseVisualStarts(long timestamp)
    {
        foreach (var state in _visuals)
        {
            var frame = state.Member.Plan.Start;
            if (_predecessor?.TryGetPresentationAt(
                    state.Member.Window,
                    timestamp,
                    out var sampled) == true)
            {
                frame = sampled;
            }

            var startBounds =
                EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(frame);
            if (startBounds.IsEmpty ||
                startBounds.Width != state.SourceBounds.Width ||
                startBounds.Height != state.SourceBounds.Height)
            {
                throw new InvalidOperationException(
                    "A successor cannot change live surface identity.");
            }

            state.StartOffsetX =
                startBounds.Left - _outputBounds.Left;
            state.StartOffsetY =
                startBounds.Top - _outputBounds.Top;
            state.Visual.SetOffsetX(
                state.StartOffsetX).CheckError();
            state.Visual.SetOffsetY(
                state.StartOffsetY).CheckError();
        }
    }

    private void ConfigureAnimations(long absoluteBeginTimestamp)
    {
        foreach (var state in _visuals)
        {
            state.OffsetXAnimation = ApplyAnimatedValue(
                state.StartOffsetX,
                state.TargetOffsetX,
                value => state.Visual.SetOffsetX(value),
                animation => state.Visual.SetOffsetX(animation),
                absoluteBeginTimestamp);
            state.OffsetYAnimation = ApplyAnimatedValue(
                state.StartOffsetY,
                state.TargetOffsetY,
                value => state.Visual.SetOffsetY(value),
                animation => state.Visual.SetOffsetY(animation),
                absoluteBeginTimestamp);
        }
    }

    private IDCompositionAnimation? ApplyAnimatedValue(
        float from,
        float to,
        Func<float, SharpGen.Runtime.Result> applyStatic,
        Func<IDCompositionAnimation, SharpGen.Runtime.Result>
            applyAnimation,
        long absoluteBeginTimestamp)
    {
        if (Math.Abs(from - to) < 0.001f)
        {
            applyStatic(to).CheckError();
            return null;
        }

        var animation = CreateEaseOutCubicAnimation(
            from,
            to,
            absoluteBeginTimestamp,
            _plan.DurationMilliseconds);
        try
        {
            applyAnimation(animation).CheckError();
            return animation;
        }
        catch
        {
            animation.Dispose();
            throw;
        }
    }

    private IDCompositionAnimation CreateEaseOutCubicAnimation(
        float from,
        float to,
        long absoluteBeginTimestamp,
        int durationMilliseconds)
    {
        var durationSeconds =
            Math.Max(0.001, durationMilliseconds / 1000.0);
        var delta = to - from;
        var animation = _device.CreateAnimation();
        try
        {
            animation.SetAbsoluteBeginTime(
                absoluteBeginTimestamp).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * delta / durationSeconds),
                (float)(-3 * delta /
                    (durationSeconds * durationSeconds)),
                (float)(delta /
                    (durationSeconds * durationSeconds *
                     durationSeconds))).CheckError();
            animation.End(durationSeconds, to).CheckError();
            return animation;
        }
        catch
        {
            animation.Dispose();
            throw;
        }
    }
}
