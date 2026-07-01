#nullable enable

namespace SmartPipe.Core;

internal sealed class AdaptiveParallelismController
{
    private readonly AdaptiveParallelismOptions _options;
    private double? _smoothedLatencyMs;

    public AdaptiveParallelismController(AdaptiveParallelismOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public AdaptiveParallelismDecision Decide(AdaptiveParallelismSnapshot snapshot)
    {
        var current = Clamp(
            snapshot.CurrentConcurrency,
            _options.MinConcurrency,
            _options.MaxConcurrency);

        if (snapshot.TimeSinceLastDecision < _options.Cooldown)
        {
            return new AdaptiveParallelismDecision(
                current,
                current,
                GetSmoothedLatency(snapshot.LatencySample).Latency,
                AdaptiveParallelismDecisionReason.Cooldown);
        }

        var smoothed = GetSmoothedLatency(snapshot.LatencySample);

        if (HasFailurePressure(snapshot))
        {
            return new AdaptiveParallelismDecision(
                current,
                DecreaseConcurrencyLimit(current),
                smoothed.Latency,
                AdaptiveParallelismDecisionReason.FailurePressure);
        }

        var error = _options.TargetLatency - smoothed.Latency;
        if (error.Duration() <= _options.DeadZone)
        {
            return new AdaptiveParallelismDecision(
                current,
                current,
                smoothed.Latency,
                AdaptiveParallelismDecisionReason.DeadZone);
        }

        if (error < TimeSpan.Zero)
        {
            if (current <= _options.MinConcurrency)
            {
                return new AdaptiveParallelismDecision(
                    current,
                    _options.MinConcurrency,
                    smoothed.Latency,
                    AdaptiveParallelismDecisionReason.AtMin);
            }

            return new AdaptiveParallelismDecision(
                current,
                DecreaseConcurrencyLimit(current),
                smoothed.Latency,
                AdaptiveParallelismDecisionReason.HighLatency);
        }

        if (current >= _options.MaxConcurrency)
        {
            return new AdaptiveParallelismDecision(
                current,
                _options.MaxConcurrency,
                smoothed.Latency,
                AdaptiveParallelismDecisionReason.AtMax);
        }

        return new AdaptiveParallelismDecision(
            current,
            IncreaseConcurrencyLimit(current),
            smoothed.Latency,
            AdaptiveParallelismDecisionReason.LowLatency);
    }

    private SmoothedLatency GetSmoothedLatency(TimeSpan sample)
    {
        var sampleMs = Math.Max(0, sample.TotalMilliseconds);
        if (_smoothedLatencyMs is null)
        {
            _smoothedLatencyMs = sampleMs;
            return new SmoothedLatency(TimeSpan.FromMilliseconds(sampleMs));
        }

        var previous = _smoothedLatencyMs.Value;
        var targetMs = Math.Max(1, _options.TargetLatency.TotalMilliseconds);
        var alpha = Math.Clamp(Math.Abs(sampleMs - previous) / targetMs, _options.MinSmoothingFactor, 1.0);
        var next = alpha * sampleMs + (1.0 - alpha) * previous;
        _smoothedLatencyMs = next;
        return new SmoothedLatency(TimeSpan.FromMilliseconds(next));
    }

    private bool HasFailurePressure(AdaptiveParallelismSnapshot snapshot)
    {
        var denominator = Math.Max(1, snapshot.ProcessedDelta);
        return (double)snapshot.FailedDelta / denominator >= _options.FailurePressureThreshold;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    private static int Clamp(long value, int min, int max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return (int)value;
    }

    private int DecreaseConcurrencyLimit(int current) =>
        Clamp((long)current - _options.MaxAdjustmentStep, _options.MinConcurrency, _options.MaxConcurrency);

    private int IncreaseConcurrencyLimit(int current) =>
        Clamp((long)current + _options.MaxAdjustmentStep, _options.MinConcurrency, _options.MaxConcurrency);

    private readonly record struct SmoothedLatency(TimeSpan Latency);
}

internal readonly record struct AdaptiveParallelismSnapshot(
    int CurrentConcurrency,
    TimeSpan LatencySample,
    long ProcessedDelta,
    long FailedDelta,
    TimeSpan TimeSinceLastDecision);

internal readonly record struct AdaptiveParallelismDecision(
    int PreviousConcurrency,
    int TargetConcurrency,
    TimeSpan SmoothedLatency,
    AdaptiveParallelismDecisionReason Reason);

internal enum AdaptiveParallelismDecisionReason
{
    Cooldown,
    DeadZone,
    HighLatency,
    LowLatency,
    AtMin,
    AtMax,
    FailurePressure,
}
