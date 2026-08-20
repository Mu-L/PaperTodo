using System.Diagnostics;

namespace PaperTodo;

internal enum EdgeCapsuleHoverIntentMode
{
    Initial,
    Transfer
}

internal enum EdgeCapsuleHoverIntentDecision
{
    NoExtraDelay,
    Delay,
    Veto
}

internal enum EdgeCapsuleCorridorExitDecision
{
    KeepAlive,
    ConfirmNoTargetIntent,
    CloseForNoTargetIntent
}

/// <summary>
/// A negative-only hover-intent gate. The live physical hit test always chooses the candidate;
/// this policy may only delay/veto an activation or release an already empty queue corridor. It
/// never chooses a destination and never opens a capsule on its own.
/// </summary>
internal sealed class EdgeCapsuleHoverIntentPredictor
{
    private const int SampleCapacity = 20;
    private const double HistoryWindowMilliseconds = 64;
    private const double StaleHistoryMilliseconds = 180;
    private const double DuplicateSampleMilliseconds = 1.5;
    private const double BrakingRatio = 0.72;
    private const double BrakingDeltaDipPerMillisecond = 0.05;
    private const double AccelerationRatio = 1.15;
    private const double AccelerationDeltaDipPerMillisecond = 0.04;

    private static readonly IntentSensitivityProfile VeryHighProfile = new(
        Initial: new IntentProfile(6, 4, 22, 55, 135),
        Transfer: new IntentProfile(10, 8, 38, 90, 190),
        StableFallbackMilliseconds: 38,
        MinimumDirectionalSpeedDipPerMillisecond: 0.13,
        MinimumDirectionConsistency: 0.76,
        MinimumVerticalDominance: 0.60,
        CorridorExit: new CorridorExitProfile(
            0.060, 0.62, 12, 200));

    private static readonly IntentSensitivityProfile HighProfile = new(
        Initial: new IntentProfile(8, 8, 32, 80, 180),
        Transfer: new IntentProfile(12, 12, 50, 120, 240),
        StableFallbackMilliseconds: 50,
        MinimumDirectionalSpeedDipPerMillisecond: 0.10,
        MinimumDirectionConsistency: 0.72,
        MinimumVerticalDominance: 0.55,
        CorridorExit: new CorridorExitProfile(
            0.075, 0.68, 16, 350));

    private static readonly IntentSensitivityProfile MediumProfile = new(
        Initial: new IntentProfile(8, 10, 36, 90, 200),
        Transfer: new IntentProfile(14, 20, 66, 155, 310),
        StableFallbackMilliseconds: 60,
        MinimumDirectionalSpeedDipPerMillisecond: 0.075,
        MinimumDirectionConsistency: 0.68,
        MinimumVerticalDominance: 0.50,
        CorridorExit: new CorridorExitProfile(
            0.090, 0.74, 20, 500));

    // "Low" describes activation sensitivity: it applies longer waits and recognizes less
    // pronounced residual motion as pass-through risk, so stopping must be more deliberate.
    private static readonly IntentSensitivityProfile LowProfile = new(
        Initial: new IntentProfile(10, 14, 44, 110, 240),
        Transfer: new IntentProfile(18, 34, 90, 205, 410),
        StableFallbackMilliseconds: 85,
        MinimumDirectionalSpeedDipPerMillisecond: 0.055,
        MinimumDirectionConsistency: 0.64,
        MinimumVerticalDominance: 0.45,
        CorridorExit: new CorridorExitProfile(
            0.110, 0.79, 24, 650));

    private static readonly IntentSensitivityProfile VeryLowProfile = new(
        Initial: new IntentProfile(12, 18, 54, 135, 300),
        Transfer: new IntentProfile(22, 48, 115, 255, 500),
        StableFallbackMilliseconds: 110,
        MinimumDirectionalSpeedDipPerMillisecond: 0.040,
        MinimumDirectionConsistency: 0.60,
        MinimumVerticalDominance: 0.40,
        CorridorExit: new CorridorExitProfile(
            0.140, 0.84, 30, 800));

    private readonly PointerSample[] _samples =
        new PointerSample[SampleCapacity];
    private int _sampleStart;
    private int _sampleCount;
    private double _dpiScaleX = 1;
    private double _dpiScaleY = 1;

    private readonly record struct PointerSample(
        DeviceScreenPoint Point,
        long Timestamp);

    private readonly record struct IntentProfile(
        double MinimumObservationMilliseconds,
        double MinimumDelayMilliseconds,
        double MaximumDelayMilliseconds,
        double PassThroughVetoHorizonMilliseconds,
        double DynamicDelayHorizonMilliseconds);

    private readonly record struct IntentSensitivityProfile(
        IntentProfile Initial,
        IntentProfile Transfer,
        double StableFallbackMilliseconds,
        double MinimumDirectionalSpeedDipPerMillisecond,
        double MinimumDirectionConsistency,
        double MinimumVerticalDominance,
        CorridorExitProfile CorridorExit);

    private readonly record struct CorridorExitProfile(
        double MinimumSpeedDipPerMillisecond,
        double MinimumPathConsistency,
        double TargetPaddingDip,
        double NoTargetIntentCloseMilliseconds);

    private readonly record struct MotionEstimate(
        bool HasMotion,
        double SignedHorizontalSpeedDipPerMillisecond,
        double SignedVerticalSpeedDipPerMillisecond,
        double RecentSpeedDipPerMillisecond,
        double RecentVerticalSpeedDipPerMillisecond,
        double PriorVerticalSpeedDipPerMillisecond,
        double PathConsistency,
        double DirectionConsistency,
        double VerticalDominance,
        bool HasSpeedTrend);

    public void Reset()
    {
        _sampleStart = 0;
        _sampleCount = 0;
        _dpiScaleX = 1;
        _dpiScaleY = 1;
    }

    public void Reset(
        DeviceScreenPoint pointer,
        long timestamp,
        double dpiScaleX,
        double dpiScaleY)
    {
        Reset();
        _dpiScaleX = NormalizeDpiScale(dpiScaleX);
        _dpiScaleY = NormalizeDpiScale(dpiScaleY);
        AddSample(new PointerSample(pointer, timestamp));
    }

    public void Observe(
        DeviceScreenPoint pointer,
        long timestamp,
        double dpiScaleX,
        double dpiScaleY)
    {
        var nextScaleX = NormalizeDpiScale(dpiScaleX);
        var nextScaleY = NormalizeDpiScale(dpiScaleY);
        if (_sampleCount > 0 &&
            (Math.Abs(_dpiScaleX - nextScaleX) > 0.001 ||
             Math.Abs(_dpiScaleY - nextScaleY) > 0.001))
        {
            Reset();
        }
        _dpiScaleX = nextScaleX;
        _dpiScaleY = nextScaleY;

        if (_sampleCount == 0)
        {
            AddSample(new PointerSample(pointer, timestamp));
            return;
        }

        var latest = SampleAt(_sampleCount - 1);
        var elapsed = ElapsedMilliseconds(latest.Timestamp, timestamp);
        if (elapsed < 0 || elapsed > StaleHistoryMilliseconds)
        {
            Reset(pointer, timestamp, nextScaleX, nextScaleY);
            return;
        }
        if (elapsed < DuplicateSampleMilliseconds)
        {
            return;
        }

        AddSample(new PointerSample(pointer, timestamp));
    }

    public EdgeCapsuleHoverIntentDecision Evaluate(
        EdgeCapsuleHoverIntentMode mode,
        string sensitivity,
        DeviceScreenRect targetBounds,
        DeviceScreenPoint pointer,
        double candidateElapsedMilliseconds,
        double stableElapsedMilliseconds)
    {
        var sensitivityProfile = ResolveSensitivityProfile(sensitivity);
        var profile = mode == EdgeCapsuleHoverIntentMode.Initial
            ? sensitivityProfile.Initial
            : sensitivityProfile.Transfer;

        // This is a deterministic escape hatch, not a positive prediction. Even a noisy motion
        // estimate cannot keep a genuinely settled pointer pending forever.
        if (stableElapsedMilliseconds >=
            sensitivityProfile.StableFallbackMilliseconds)
        {
            return EdgeCapsuleHoverIntentDecision.NoExtraDelay;
        }

        if (candidateElapsedMilliseconds <
            profile.MinimumObservationMilliseconds)
        {
            return EdgeCapsuleHoverIntentDecision.Delay;
        }

        var motion = EstimateMotion();
        if (!motion.HasMotion ||
            motion.RecentVerticalSpeedDipPerMillisecond <
                sensitivityProfile.MinimumDirectionalSpeedDipPerMillisecond ||
            motion.DirectionConsistency <
                sensitivityProfile.MinimumDirectionConsistency ||
            motion.VerticalDominance <
                sensitivityProfile.MinimumVerticalDominance)
        {
            return stableElapsedMilliseconds >=
                profile.MinimumDelayMilliseconds
                ? EdgeCapsuleHoverIntentDecision.NoExtraDelay
                : EdgeCapsuleHoverIntentDecision.Delay;
        }

        var braking = motion.HasSpeedTrend &&
            motion.RecentVerticalSpeedDipPerMillisecond <=
                motion.PriorVerticalSpeedDipPerMillisecond * BrakingRatio &&
            motion.PriorVerticalSpeedDipPerMillisecond -
                motion.RecentVerticalSpeedDipPerMillisecond >=
                BrakingDeltaDipPerMillisecond;
        var accelerating = motion.HasSpeedTrend &&
            motion.RecentVerticalSpeedDipPerMillisecond >=
                motion.PriorVerticalSpeedDipPerMillisecond *
                    AccelerationRatio &&
            motion.RecentVerticalSpeedDipPerMillisecond -
                motion.PriorVerticalSpeedDipPerMillisecond >=
                AccelerationDeltaDipPerMillisecond;

        var distanceToExitDevice =
            motion.SignedVerticalSpeedDipPerMillisecond < 0
            ? pointer.Y - targetBounds.Top
            : targetBounds.Bottom - pointer.Y;
        var distanceToExitDip = distanceToExitDevice / _dpiScaleY;
        var timeToExit = Math.Max(0, distanceToExitDip) /
            motion.RecentVerticalSpeedDipPerMillisecond;

        // menu-aim style negative protection: a coherent, non-braking trajectory that will leave
        // this physical target soon is a pass-through, so it cannot activate this target. Strong
        // acceleration broadens the protection slightly; braking removes it immediately.
        var vetoHorizon = profile.PassThroughVetoHorizonMilliseconds *
            (accelerating ? 1.20 : 1.0);
        if (!braking && timeToExit <= vetoHorizon)
        {
            return EdgeCapsuleHoverIntentDecision.Veto;
        }

        // hoverIntent style adaptive dwell: faster, persistent motion consumes more of the bounded
        // delay budget. A clear braking trend selects the short end of that budget, while the
        // stable clock prevents time accumulated during earlier movement from authorizing the
        // target immediately after the pointer shifts again.
        var risk = Math.Clamp(
            1 - timeToExit / profile.DynamicDelayHorizonMilliseconds,
            0,
            1);
        if (accelerating)
        {
            risk = Math.Min(1, risk + 0.20);
        }
        else if (braking)
        {
            risk *= 0.25;
        }
        else if (!motion.HasSpeedTrend)
        {
            risk *= 0.85;
        }

        var requiredDelay = profile.MinimumDelayMilliseconds +
            (profile.MaximumDelayMilliseconds -
                profile.MinimumDelayMilliseconds) * risk;
        return stableElapsedMilliseconds >= requiredDelay
            ? EdgeCapsuleHoverIntentDecision.NoExtraDelay
            : EdgeCapsuleHoverIntentDecision.Delay;
    }

    /// <summary>
    /// Evaluates only the empty area inside the queue's outer corridor. The currently open card and
    /// every eligible compact capsule are supplied as keep-alive bounds. A coherent movement ray
    /// toward any of them keeps the session alive. Every other trajectory, including a settled
    /// pointer, shares one sensitivity-dependent no-target-intent deadline.
    /// </summary>
    public EdgeCapsuleCorridorExitDecision EvaluateCorridorExit(
        string sensitivity,
        ReadOnlySpan<DeviceScreenRect> keepAliveBounds,
        DeviceScreenPoint pointer,
        double noTargetIntentElapsedMilliseconds)
    {
        var profile = ResolveSensitivityProfile(sensitivity).CorridorExit;
        var motion = EstimateMotion();
        if (motion.HasMotion &&
            motion.RecentSpeedDipPerMillisecond >=
                profile.MinimumSpeedDipPerMillisecond &&
            motion.PathConsistency >= profile.MinimumPathConsistency)
        {
            var directionX =
                motion.SignedHorizontalSpeedDipPerMillisecond *
                _dpiScaleX;
            var directionY =
                motion.SignedVerticalSpeedDipPerMillisecond *
                _dpiScaleY;
            var horizontalPadding =
                profile.TargetPaddingDip * _dpiScaleX;
            var verticalPadding =
                profile.TargetPaddingDip * _dpiScaleY;
            foreach (var bounds in keepAliveBounds)
            {
                if (RayHeadsTowardBounds(
                        pointer,
                        directionX,
                        directionY,
                        bounds) &&
                    RayIntersectsBounds(
                        pointer,
                        directionX,
                        directionY,
                        bounds,
                        horizontalPadding,
                        verticalPadding))
                {
                    return EdgeCapsuleCorridorExitDecision.KeepAlive;
                }
            }
        }

        return noTargetIntentElapsedMilliseconds >=
            profile.NoTargetIntentCloseMilliseconds
            ? EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent
            : EdgeCapsuleCorridorExitDecision.ConfirmNoTargetIntent;
    }

    public double CorridorNoTargetIntentCloseMilliseconds(string sensitivity) =>
        ResolveSensitivityProfile(sensitivity)
            .CorridorExit
            .NoTargetIntentCloseMilliseconds;

    private MotionEstimate EstimateMotion()
    {
        if (_sampleCount < 2)
        {
            return default;
        }

        var latest = SampleAt(_sampleCount - 1);
        var firstIndex = _sampleCount - 2;
        while (firstIndex > 0 &&
            ElapsedMilliseconds(
                SampleAt(firstIndex - 1).Timestamp,
                latest.Timestamp) <= HistoryWindowMilliseconds)
        {
            firstIndex--;
        }

        var first = SampleAt(firstIndex);
        var duration = ElapsedMilliseconds(first.Timestamp, latest.Timestamp);
        if (duration <= 0)
        {
            return default;
        }

        var totalDistance = 0.0;
        var totalVerticalDistance = 0.0;
        for (var index = firstIndex + 1;
            index < _sampleCount;
            index++)
        {
            var previous = SampleAt(index - 1).Point;
            var current = SampleAt(index).Point;
            var deltaX = (current.X - previous.X) / _dpiScaleX;
            var deltaY = (current.Y - previous.Y) / _dpiScaleY;
            totalDistance += Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            totalVerticalDistance += Math.Abs(deltaY);
        }

        var netHorizontalDistance =
            (latest.Point.X - first.Point.X) / _dpiScaleX;
        var netVerticalDistance =
            (latest.Point.Y - first.Point.Y) / _dpiScaleY;
        var absoluteNetVerticalDistance = Math.Abs(netVerticalDistance);
        if (totalDistance <= double.Epsilon)
        {
            return default;
        }

        var midpointTimestamp = first.Timestamp +
            (latest.Timestamp - first.Timestamp) / 2;
        var midpointIndex = firstIndex;
        for (var index = firstIndex + 1;
            index < _sampleCount - 1;
            index++)
        {
            if (SampleAt(index).Timestamp <= midpointTimestamp)
            {
                midpointIndex = index;
                continue;
            }
            break;
        }

        var midpoint = SampleAt(midpointIndex);
        var recentDuration = ElapsedMilliseconds(
            midpoint.Timestamp,
            latest.Timestamp);
        if (recentDuration <= 0)
        {
            midpoint = first;
            recentDuration = duration;
        }

        var recentHorizontalDelta =
            (latest.Point.X - midpoint.Point.X) / _dpiScaleX;
        var recentVerticalDelta =
            (latest.Point.Y - midpoint.Point.Y) / _dpiScaleY;
        var recentSignedHorizontalSpeed =
            recentHorizontalDelta / recentDuration;
        var recentSignedVerticalSpeed =
            recentVerticalDelta / recentDuration;
        var recentVerticalSpeed = Math.Abs(recentSignedVerticalSpeed);
        var recentSpeed = Math.Sqrt(
            recentSignedHorizontalSpeed * recentSignedHorizontalSpeed +
            recentSignedVerticalSpeed * recentSignedVerticalSpeed);
        var priorDuration = ElapsedMilliseconds(
            first.Timestamp,
            midpoint.Timestamp);
        var priorVerticalSpeed = priorDuration > 0
            ? Math.Abs(midpoint.Point.Y - first.Point.Y) /
                _dpiScaleY /
                priorDuration
            : recentVerticalSpeed;

        return new MotionEstimate(
            HasMotion: true,
            SignedHorizontalSpeedDipPerMillisecond:
                recentSignedHorizontalSpeed,
            SignedVerticalSpeedDipPerMillisecond:
                recentSignedVerticalSpeed,
            RecentSpeedDipPerMillisecond: recentSpeed,
            RecentVerticalSpeedDipPerMillisecond:
                recentVerticalSpeed,
            PriorVerticalSpeedDipPerMillisecond:
                priorVerticalSpeed,
            PathConsistency: Math.Sqrt(
                netHorizontalDistance * netHorizontalDistance +
                netVerticalDistance * netVerticalDistance) /
                totalDistance,
            DirectionConsistency:
                totalVerticalDistance > double.Epsilon
                    ? absoluteNetVerticalDistance / totalVerticalDistance
                    : 0,
            VerticalDominance:
                absoluteNetVerticalDistance / totalDistance,
            HasSpeedTrend: priorDuration > 0);
    }

    private static bool RayHeadsTowardBounds(
        DeviceScreenPoint start,
        double directionX,
        double directionY,
        DeviceScreenRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        // Padding makes near-misses forgiving, but it must not make t=0 an automatic hit when the
        // pointer has just left a real capsule. Require progress toward the closest point of the
        // unpadded target. The blank-region caller normally excludes real hits; keep the center
        // fallback only so this helper remains well-defined if it is given a point inside one.
        // DeviceScreenRect is half-open and physical pointer coordinates are device pixels. Use
        // the last hittable pixel rather than the exclusive Right/Bottom edge as the real target.
        var closestX = Math.Clamp(start.X, bounds.Left, bounds.Right - 1.0);
        var closestY = Math.Clamp(start.Y, bounds.Top, bounds.Bottom - 1.0);
        var towardX = closestX - start.X;
        var towardY = closestY - start.Y;
        if (Math.Abs(towardX) <= double.Epsilon &&
            Math.Abs(towardY) <= double.Epsilon)
        {
            towardX = (bounds.Left + bounds.Right) / 2.0 - start.X;
            towardY = (bounds.Top + bounds.Bottom) / 2.0 - start.Y;
        }
        return directionX * towardX + directionY * towardY > 0;
    }

    private static bool RayIntersectsBounds(
        DeviceScreenPoint start,
        double directionX,
        double directionY,
        DeviceScreenRect bounds,
        double horizontalPadding,
        double verticalPadding)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        var minimumX = bounds.Left - Math.Max(0, horizontalPadding);
        var maximumX = bounds.Right + Math.Max(0, horizontalPadding);
        var minimumY = bounds.Top - Math.Max(0, verticalPadding);
        var maximumY = bounds.Bottom + Math.Max(0, verticalPadding);
        var minimumTime = 0.0;
        var maximumTime = double.PositiveInfinity;
        return RayAxisIntersects(
                start.X,
                directionX,
                minimumX,
                maximumX,
                ref minimumTime,
                ref maximumTime) &&
            RayAxisIntersects(
                start.Y,
                directionY,
                minimumY,
                maximumY,
                ref minimumTime,
                ref maximumTime);
    }

    private static bool RayAxisIntersects(
        double start,
        double delta,
        double minimum,
        double maximum,
        ref double minimumTime,
        ref double maximumTime)
    {
        if (Math.Abs(delta) <= double.Epsilon)
        {
            return start >= minimum && start <= maximum;
        }

        var first = (minimum - start) / delta;
        var second = (maximum - start) / delta;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        minimumTime = Math.Max(minimumTime, first);
        maximumTime = Math.Min(maximumTime, second);
        return minimumTime <= maximumTime;
    }

    private static IntentSensitivityProfile ResolveSensitivityProfile(
        string sensitivity)
    {
        return EdgeCapsuleHoverIntentSensitivities.Normalize(sensitivity) switch
        {
            EdgeCapsuleHoverIntentSensitivities.VeryLow => VeryLowProfile,
            EdgeCapsuleHoverIntentSensitivities.Low => LowProfile,
            EdgeCapsuleHoverIntentSensitivities.High => HighProfile,
            EdgeCapsuleHoverIntentSensitivities.VeryHigh => VeryHighProfile,
            _ => MediumProfile
        };
    }

    private static double NormalizeDpiScale(double scale) =>
        double.IsFinite(scale) ? Math.Max(1, scale) : 1;

    private void AddSample(PointerSample sample)
    {
        if (_sampleCount < SampleCapacity)
        {
            var destination = (_sampleStart + _sampleCount) %
                SampleCapacity;
            _samples[destination] = sample;
            _sampleCount++;
            return;
        }

        _samples[_sampleStart] = sample;
        _sampleStart = (_sampleStart + 1) % SampleCapacity;
    }

    private PointerSample SampleAt(int index) =>
        _samples[(_sampleStart + index) % SampleCapacity];

    private static double ElapsedMilliseconds(long start, long end) =>
        Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
}
