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

/// <summary>
/// A negative-only hover-intent gate. The live physical hit test always chooses the candidate;
/// this policy may only add a short delay or veto an obvious pass-through. It never predicts a
/// destination and never opens a capsule on its own.
/// </summary>
internal sealed class EdgeCapsuleHoverIntentPredictor
{
    private const int SampleCapacity = 20;
    private const double HistoryWindowMilliseconds = 64;
    private const double StaleHistoryMilliseconds = 180;
    private const double DuplicateSampleMilliseconds = 1.5;
    private const double StableFallbackMilliseconds = 50;
    private const double MinimumDirectionalSpeedDevicePerMillisecond = 0.10;
    private const double MinimumDirectionConsistency = 0.72;
    private const double MinimumVerticalDominance = 0.55;
    private const double BrakingRatio = 0.72;
    private const double BrakingDeltaDevicePerMillisecond = 0.06;
    private const double AccelerationRatio = 1.15;
    private const double AccelerationDeltaDevicePerMillisecond = 0.05;

    private static readonly IntentProfile InitialProfile = new(
        MinimumObservationMilliseconds: 8,
        MinimumDelayMilliseconds: 8,
        MaximumDelayMilliseconds: 32,
        PassThroughVetoHorizonMilliseconds: 80,
        DynamicDelayHorizonMilliseconds: 180);

    private static readonly IntentProfile TransferProfile = new(
        MinimumObservationMilliseconds: 12,
        MinimumDelayMilliseconds: 12,
        MaximumDelayMilliseconds: 50,
        PassThroughVetoHorizonMilliseconds: 120,
        DynamicDelayHorizonMilliseconds: 240);

    private readonly PointerSample[] _samples =
        new PointerSample[SampleCapacity];
    private int _sampleStart;
    private int _sampleCount;

    private readonly record struct PointerSample(
        DeviceScreenPoint Point,
        long Timestamp);

    private readonly record struct IntentProfile(
        double MinimumObservationMilliseconds,
        double MinimumDelayMilliseconds,
        double MaximumDelayMilliseconds,
        double PassThroughVetoHorizonMilliseconds,
        double DynamicDelayHorizonMilliseconds);

    private readonly record struct MotionEstimate(
        bool HasMotion,
        double SignedVerticalSpeedDevicePerMillisecond,
        double RecentVerticalSpeedDevicePerMillisecond,
        double PriorVerticalSpeedDevicePerMillisecond,
        double DirectionConsistency,
        double VerticalDominance,
        bool HasSpeedTrend);

    public void Reset()
    {
        _sampleStart = 0;
        _sampleCount = 0;
    }

    public void Reset(DeviceScreenPoint pointer, long timestamp)
    {
        Reset();
        AddSample(new PointerSample(pointer, timestamp));
    }

    public void Observe(DeviceScreenPoint pointer, long timestamp)
    {
        if (_sampleCount == 0)
        {
            AddSample(new PointerSample(pointer, timestamp));
            return;
        }

        var latest = SampleAt(_sampleCount - 1);
        var elapsed = ElapsedMilliseconds(latest.Timestamp, timestamp);
        if (elapsed < 0 || elapsed > StaleHistoryMilliseconds)
        {
            Reset(pointer, timestamp);
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
        DeviceScreenRect targetBounds,
        DeviceScreenPoint pointer,
        double candidateElapsedMilliseconds,
        double stableElapsedMilliseconds)
    {
        // This is a deterministic escape hatch, not a positive prediction. Even a noisy motion
        // estimate cannot keep a genuinely settled pointer pending forever.
        if (stableElapsedMilliseconds >= StableFallbackMilliseconds)
        {
            return EdgeCapsuleHoverIntentDecision.NoExtraDelay;
        }

        var profile = mode == EdgeCapsuleHoverIntentMode.Initial
            ? InitialProfile
            : TransferProfile;
        if (candidateElapsedMilliseconds <
            profile.MinimumObservationMilliseconds)
        {
            return EdgeCapsuleHoverIntentDecision.Delay;
        }

        var motion = EstimateMotion();
        if (!motion.HasMotion ||
            motion.RecentVerticalSpeedDevicePerMillisecond <
                MinimumDirectionalSpeedDevicePerMillisecond ||
            motion.DirectionConsistency < MinimumDirectionConsistency ||
            motion.VerticalDominance < MinimumVerticalDominance)
        {
            return EdgeCapsuleHoverIntentDecision.NoExtraDelay;
        }

        var braking = motion.HasSpeedTrend &&
            motion.RecentVerticalSpeedDevicePerMillisecond <=
                motion.PriorVerticalSpeedDevicePerMillisecond * BrakingRatio &&
            motion.PriorVerticalSpeedDevicePerMillisecond -
                motion.RecentVerticalSpeedDevicePerMillisecond >=
                BrakingDeltaDevicePerMillisecond;
        var accelerating = motion.HasSpeedTrend &&
            motion.RecentVerticalSpeedDevicePerMillisecond >=
                motion.PriorVerticalSpeedDevicePerMillisecond *
                    AccelerationRatio &&
            motion.RecentVerticalSpeedDevicePerMillisecond -
                motion.PriorVerticalSpeedDevicePerMillisecond >=
                AccelerationDeltaDevicePerMillisecond;

        var distanceToExit = motion.SignedVerticalSpeedDevicePerMillisecond < 0
            ? pointer.Y - targetBounds.Top
            : targetBounds.Bottom - pointer.Y;
        var timeToExit = Math.Max(0, distanceToExit) /
            motion.RecentVerticalSpeedDevicePerMillisecond;

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
        // delay budget. A clear braking trend sharply reduces the remaining delay, so stopping on
        // a capsule normally releases the gate on the next frame instead of waiting the full 50ms.
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
        return candidateElapsedMilliseconds >= requiredDelay
            ? EdgeCapsuleHoverIntentDecision.NoExtraDelay
            : EdgeCapsuleHoverIntentDecision.Delay;
    }

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
            var deltaX = current.X - previous.X;
            var deltaY = current.Y - previous.Y;
            totalDistance += Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            totalVerticalDistance += Math.Abs(deltaY);
        }

        var netVerticalDistance = latest.Point.Y - first.Point.Y;
        var absoluteNetVerticalDistance = Math.Abs(netVerticalDistance);
        if (totalDistance <= double.Epsilon ||
            totalVerticalDistance <= double.Epsilon)
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

        var recentVerticalDelta = latest.Point.Y - midpoint.Point.Y;
        var recentSignedVerticalSpeed =
            recentVerticalDelta / recentDuration;
        var recentVerticalSpeed = Math.Abs(recentSignedVerticalSpeed);
        var priorDuration = ElapsedMilliseconds(
            first.Timestamp,
            midpoint.Timestamp);
        var priorVerticalSpeed = priorDuration > 0
            ? Math.Abs(midpoint.Point.Y - first.Point.Y) / priorDuration
            : recentVerticalSpeed;

        return new MotionEstimate(
            HasMotion: true,
            SignedVerticalSpeedDevicePerMillisecond:
                recentSignedVerticalSpeed,
            RecentVerticalSpeedDevicePerMillisecond:
                recentVerticalSpeed,
            PriorVerticalSpeedDevicePerMillisecond:
                priorVerticalSpeed,
            DirectionConsistency:
                absoluteNetVerticalDistance / totalVerticalDistance,
            VerticalDominance:
                absoluteNetVerticalDistance / totalDistance,
            HasSpeedTrend: priorDuration > 0);
    }

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
