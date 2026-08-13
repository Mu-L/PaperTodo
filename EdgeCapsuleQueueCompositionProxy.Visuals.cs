using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private VisualState AddVisual(
        EdgeCapsuleQueueCompositionProxyMember member,
        IntPtr sourceHandle,
        DeviceScreenRect sourceBounds,
        DeviceScreenRect initialVisualBounds,
        float initialOpacity,
        bool endpointLayer,
        IDCompositionVisual? referenceVisual,
        IUnknown? existingSurface = null)
    {
        IUnknown? surface = existingSurface;
        IDCompositionVisual? visual = null;
        IDCompositionScaleTransform? scale = null;
        IDCompositionEffectGroup? effect = null;
        try
        {
            if (surface == null)
            {
                _device.CreateSurfaceFromHwnd(sourceHandle, out var createdSurface)
                    .CheckError();
                surface = createdSurface;
            }
            _device.CreateVisual(out IDCompositionVisual2 createdVisual).CheckError();
            visual = createdVisual;
            visual.SetContent(surface).CheckError();

            var sourceWidth = Math.Max(1, sourceBounds.Width);
            var sourceHeight = Math.Max(1, sourceBounds.Height);
            var initialScaleX = initialVisualBounds.Width / (float)sourceWidth;
            var initialScaleY = initialVisualBounds.Height / (float)sourceHeight;
            scale = _device.CreateScaleTransform();
            scale.SetCenterX(
                EdgeCapsuleQueueProxyGeometry.ScaleCenterX(
                    member.Plan.Start.Edge,
                    sourceWidth)).CheckError();
            scale.SetCenterY(0).CheckError();
            scale.SetScaleX(initialScaleX).CheckError();
            scale.SetScaleY(initialScaleY).CheckError();
            visual.SetTransform(scale).CheckError();

            // Horizontal attachment is algebraic: the source's wall-side edge is the transform
            // centre, and the visual's X offset never animates. Width can no longer drift inward
            // for one frame and then be corrected by an unrelated offset sample.
            visual.SetOffsetX(
                EdgeCapsuleQueueProxyGeometry.WallPinnedOffsetX(
                    member.Plan.Start.Edge,
                    member.Plan.Start.WallDeviceX,
                    sourceWidth,
                    _outputBounds)).CheckError();
            visual.SetOffsetY(initialVisualBounds.Top - _outputBounds.Top).CheckError();

            effect = _device.CreateEffectGroup();
            effect.SetOpacity(initialOpacity).CheckError();
            visual.SetEffect(effect).CheckError();
            _root.AddVisual(
                visual,
                insertAbove: true,
                referenceVisual: referenceVisual!).CheckError();

            var state = new VisualState
            {
                Member = member,
                PresentedSourceHandle = sourceHandle,
                SourceBounds = sourceBounds,
                Surface = surface,
                Visual = visual,
                Effect = effect,
                Scale = scale,
                IsEndpointLayer = endpointLayer
            };
            _visuals.Add(state);
            surface = null;
            visual = null;
            effect = null;
            scale = null;
            return state;
        }
        finally
        {
            scale?.Dispose();
            effect?.Dispose();
            visual?.Dispose();
            surface?.Dispose();
        }
    }

    private void ConfigureAnimations(VisualState state)
    {
        var member = state.Member;
        var targetBounds = member.Plan.Target.Bounds;
        var sourceWidth = Math.Max(1, state.SourceBounds.Width);
        var sourceHeight = Math.Max(1, state.SourceBounds.Height);
        var targetScaleX = state.IsEndpointLayer
            ? 1
            : targetBounds.Width / (float)sourceWidth;
        var targetScaleY = state.IsEndpointLayer
            ? 1
            : targetBounds.Height / (float)sourceHeight;
        var targetOffsetY = targetBounds.Top - _outputBounds.Top;

        IDCompositionAnimation? offsetY = null;
        IDCompositionAnimation? scaleX = null;
        IDCompositionAnimation? scaleY = null;
        IDCompositionAnimation? opacity = null;
        try
        {
            var initialBounds = state.IsEndpointLayer
                ? member.Plan.Start.Bounds
                : state.SourceBounds;
            offsetY = CreateEaseOutCubicAnimation(
                initialBounds.Top - _outputBounds.Top,
                targetOffsetY,
                _plan.DurationMilliseconds);
            scaleX = CreateEaseOutCubicAnimation(
                initialBounds.Width / (float)sourceWidth,
                targetScaleX,
                _plan.DurationMilliseconds);
            scaleY = CreateEaseOutCubicAnimation(
                initialBounds.Height / (float)sourceHeight,
                targetScaleY,
                _plan.DurationMilliseconds);

            state.Visual.SetOffsetY(offsetY).CheckError();
            state.Scale.SetScaleX(scaleX).CheckError();
            state.Scale.SetScaleY(scaleY).CheckError();

            // Any source whose real HWND changes shape/content under the cover uses two immutable
            // layers. Crossfade them while both follow identical wall-anchored geometry; preview
            // opening and compact hover resize therefore share one compositor transaction.
            if (member.Plan.UsesEndpointLayer)
            {
                opacity = CreateEaseOutCubicAnimation(
                    state.IsEndpointLayer ? 0 : 1,
                    state.IsEndpointLayer ? 1 : 0,
                    _plan.DurationMilliseconds);
                state.Effect.SetOpacity(opacity).CheckError();
            }

            state.OffsetYAnimation = offsetY;
            state.ScaleXAnimation = scaleX;
            state.ScaleYAnimation = scaleY;
            state.OpacityAnimation = opacity;
            offsetY = null;
            scaleX = null;
            scaleY = null;
            opacity = null;
        }
        finally
        {
            opacity?.Dispose();
            scaleY?.Dispose();
            scaleX?.Dispose();
            offsetY?.Dispose();
        }
    }

    private IDCompositionAnimation CreateEaseOutCubicAnimation(
        float from,
        float to,
        int durationMilliseconds)
    {
        var durationSeconds = Math.Max(0.001, durationMilliseconds / 1000.0);
        var delta = to - from;
        var animation = _device.CreateAnimation();
        try
        {
            animation.AddCubic(
                0,
                from,
                (float)(3 * delta / durationSeconds),
                (float)(-3 * delta / (durationSeconds * durationSeconds)),
                (float)(delta / (durationSeconds * durationSeconds * durationSeconds)))
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