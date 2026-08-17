using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private VisualState AddVisual(
        EdgeCapsuleQueueCompositionProxyMember member,
        EdgeCapsuleQueueProxyVisualLayer layer,
        IntPtr sourceHandle,
        DeviceScreenRect sourceBounds,
        DeviceScreenRect startVisualBounds,
        DeviceScreenRect targetVisualBounds,
        EdgeCapsuleProxyClipRect startClip,
        EdgeCapsuleProxyClipRect targetClip,
        float startOpacity,
        float targetOpacity,
        IDCompositionVisual? referenceVisual,
        bool insertAbove = true,
        IUnknown? existingSurface = null)
    {
        IUnknown? surface = existingSurface;
        IDCompositionVisual? visual = null;
        IDCompositionEffectGroup? effect = null;
        IDCompositionRectangleClip? clip = null;
        try
        {
            if (surface == null)
            {
                _device.CreateSurfaceFromHwnd(
                    sourceHandle,
                    out var createdSurface).CheckError();
                surface = createdSurface;
            }

            _device.CreateVisual(
                out IDCompositionVisual2 createdVisual).CheckError();
            visual = createdVisual;
            visual.SetContent(surface).CheckError();
            // DirectComposition resolves an all-INHERIT tree to aliased clip edges. Fractional
            // animation offsets also require linear sampling; set both explicitly on every layer.
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

            clip = _device.CreateRectangleClip();
            ApplyClip(clip, startClip);
            ApplyClipRadii(
                clip,
                member,
                layer,
                startClip,
                targetClip);
            visual.SetClip(clip).CheckError();

            effect = _device.CreateEffectGroup();
            effect.SetOpacity(startOpacity).CheckError();
            visual.SetEffect(effect).CheckError();

            _root.AddVisual(
                visual,
                insertAbove,
                referenceVisual!).CheckError();

            var state = new VisualState
            {
                Member = member,
                Layer = layer,
                PresentedSourceHandle = sourceHandle,
                SourceBounds = sourceBounds,
                Surface = surface,
                Visual = visual,
                Effect = effect,
                Clip = clip,
                StartOffsetX = startOffsetX,
                StartOffsetY = startOffsetY,
                TargetOffsetX = targetOffsetX,
                TargetOffsetY = targetOffsetY,
                StartClip = startClip,
                TargetClip = targetClip,
                StartOpacity = startOpacity,
                TargetOpacity = targetOpacity,
                OpacityDurationMilliseconds =
                    layer == EdgeCapsuleQueueProxyVisualLayer.StartSnapshot
                        ? 0
                        : _plan.DurationMilliseconds
            };
            _visuals.Add(state);
            surface = null;
            visual = null;
            effect = null;
            clip = null;
            return state;
        }
        finally
        {
            clip?.Dispose();
            effect?.Dispose();
            visual?.Dispose();
            surface?.Dispose();
        }
    }

    private void ConfigureAnimations()
    {
        foreach (var state in _visuals)
        {
            ConfigureAnimations(state);
        }
    }

    private void ConfigureAnimations(VisualState state)
    {
        state.OffsetXAnimation = ApplyAnimatedValue(
            state.StartOffsetX,
            state.TargetOffsetX,
            value => state.Visual.SetOffsetX(value),
            animation => state.Visual.SetOffsetX(animation));
        state.OffsetYAnimation = ApplyAnimatedValue(
            state.StartOffsetY,
            state.TargetOffsetY,
            value => state.Visual.SetOffsetY(value),
            animation => state.Visual.SetOffsetY(animation));

        state.ClipLeftAnimation = ApplyAnimatedValue(
            state.StartClip.Left,
            state.TargetClip.Left,
            value => state.Clip.SetLeft(value),
            animation => state.Clip.SetLeft(animation));
        state.ClipTopAnimation = ApplyAnimatedValue(
            state.StartClip.Top,
            state.TargetClip.Top,
            value => state.Clip.SetTop(value),
            animation => state.Clip.SetTop(animation));
        state.ClipRightAnimation = ApplyAnimatedValue(
            state.StartClip.Right,
            state.TargetClip.Right,
            value => state.Clip.SetRight(value),
            animation => state.Clip.SetRight(animation));
        state.ClipBottomAnimation = ApplyAnimatedValue(
            state.StartClip.Bottom,
            state.TargetClip.Bottom,
            value => state.Clip.SetBottom(value),
            animation => state.Clip.SetBottom(animation));

        state.OpacityAnimation = ApplyAnimatedValue(
            state.StartOpacity,
            state.TargetOpacity,
            value => state.Effect.SetOpacity(value),
            animation => state.Effect.SetOpacity(animation),
            state.OpacityDurationMilliseconds);
    }

    private IDCompositionAnimation? ApplyAnimatedValue(
        float from,
        float to,
        Func<float, SharpGen.Runtime.Result> applyStatic,
        Func<IDCompositionAnimation, SharpGen.Runtime.Result> applyAnimation,
        int? durationMilliseconds = null)
    {
        if (durationMilliseconds is <= 0)
        {
            applyStatic(to).CheckError();
            return null;
        }
        if (Math.Abs(from - to) < 0.001f)
        {
            applyStatic(to).CheckError();
            return null;
        }

        var animation = CreateEaseOutCubicAnimation(
            from,
            to,
            durationMilliseconds ?? _plan.DurationMilliseconds);
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

    private static void ApplyClip(
        IDCompositionRectangleClip clip,
        EdgeCapsuleProxyClipRect value)
    {
        clip.SetLeft(value.Left).CheckError();
        clip.SetTop(value.Top).CheckError();
        clip.SetRight(value.Right).CheckError();
        clip.SetBottom(value.Bottom).CheckError();
    }

    private static void ApplyClipRadii(
        IDCompositionRectangleClip clip,
        EdgeCapsuleQueueCompositionProxyMember member,
        EdgeCapsuleQueueProxyVisualLayer layer,
        EdgeCapsuleProxyClipRect start,
        EdgeCapsuleProxyClipRect target)
    {
        // Moving and snapshot layers already carry their native WPF silhouette. Reveal/conceal
        // clips create a new moving viewport edge, so only the screen-internal corners are rounded;
        // the monitor-wall side remains square throughout the transition.
        if (layer is EdgeCapsuleQueueProxyVisualLayer.MovingSource or
            EdgeCapsuleQueueProxyVisualLayer.StartSnapshot)
        {
            return;
        }

        var smallestWidth = Math.Max(
            1,
            Math.Min(start.Width, target.Width));
        var smallestHeight = Math.Max(
            1,
            Math.Min(start.Height, target.Height));
        var radiusX =
            EdgeCapsuleQueueProxyGeometry
                .RoundedBodyClipRadius(
                    member.Plan.Target.DpiScaleX,
                    smallestWidth);
        var radiusY =
            EdgeCapsuleQueueProxyGeometry
                .RoundedBodyClipRadius(
                    member.Plan.Target.DpiScaleY,
                    smallestHeight);

        var leftRadiusX =
            member.Plan.Target.Edge == EdgeCapsuleEdge.Right
                ? radiusX
                : 0;
        var leftRadiusY =
            member.Plan.Target.Edge == EdgeCapsuleEdge.Right
                ? radiusY
                : 0;
        var rightRadiusX =
            member.Plan.Target.Edge == EdgeCapsuleEdge.Left
                ? radiusX
                : 0;
        var rightRadiusY =
            member.Plan.Target.Edge == EdgeCapsuleEdge.Left
                ? radiusY
                : 0;

        clip.SetTopLeftRadiusX(leftRadiusX).CheckError();
        clip.SetTopLeftRadiusY(leftRadiusY).CheckError();
        clip.SetBottomLeftRadiusX(leftRadiusX).CheckError();
        clip.SetBottomLeftRadiusY(leftRadiusY).CheckError();
        clip.SetTopRightRadiusX(rightRadiusX).CheckError();
        clip.SetTopRightRadiusY(rightRadiusY).CheckError();
        clip.SetBottomRightRadiusX(rightRadiusX).CheckError();
        clip.SetBottomRightRadiusY(rightRadiusY).CheckError();
    }

    private IDCompositionAnimation CreateEaseOutCubicAnimation(
        float from,
        float to,
        int durationMilliseconds)
    {
        var durationSeconds =
            Math.Max(0.001, durationMilliseconds / 1000.0);
        var delta = to - from;
        var animation = _device.CreateAnimation();
        try
        {
            animation.AddCubic(
                0,
                from,
                (float)(3 * delta / durationSeconds),
                (float)(-3 * delta /
                    (durationSeconds * durationSeconds)),
                (float)(delta /
                    (durationSeconds * durationSeconds * durationSeconds)))
                .CheckError();
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
