#nullable enable

namespace SmartPipe.Core;

internal sealed class AdaptiveParallelismRuntimeState : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IPipelineClock _clock;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly AdaptiveParallelismController _controller;
    private long _lastLimitChangeTimestamp;
    private bool _completed;

    public AdaptiveParallelismRuntimeState(PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var adaptive = options.AdaptiveParallelism;
        var effectiveAdaptiveMax = Math.Min(
            options.EffectiveMaxConcurrency,
            adaptive.MaxConcurrency);
        var initialLimit = Math.Clamp(
            adaptive.InitialConcurrency,
            adaptive.MinConcurrency,
            effectiveAdaptiveMax);
        var controllerOptions = new AdaptiveParallelismOptions
        {
            Enabled = true,
            MinConcurrency = adaptive.MinConcurrency,
            MaxConcurrency = effectiveAdaptiveMax,
            InitialConcurrency = initialLimit,
            TargetLatency = adaptive.TargetLatency,
            DeadZone = adaptive.DeadZone,
            Cooldown = adaptive.Cooldown,
            SampleInterval = adaptive.SampleInterval,
            MaxAdjustmentStep = adaptive.MaxAdjustmentStep,
            FailurePressureThreshold = adaptive.FailurePressureThreshold,
            MinSmoothingFactor = adaptive.MinSmoothingFactor,
        };

        _clock = options.Clock;
        _limiter = new AdaptiveConcurrencyLimiter(initialLimit, effectiveAdaptiveMax);
        _controller = new AdaptiveParallelismController(controllerOptions);
        _lastLimitChangeTimestamp = _clock.GetTimestamp();
    }

    public int CurrentLimit => _limiter.CurrentLimit;

    public ValueTask<AdaptiveConcurrencyLimiter.Lease> AcquireAsync(CancellationToken ct) =>
        _limiter.AcquireAsync(ct);

    public void RecordCompletion(TimeSpan latency, bool failed)
    {
        lock (_gate)
        {
            if (_completed)
                return;

            var now = _clock.GetTimestamp();
            var decision = _controller.Decide(new AdaptiveParallelismSnapshot(
                _limiter.CurrentLimit,
                latency,
                ProcessedDelta: 1,
                FailedDelta: failed ? 1 : 0,
                RetriedDelta: 0,
                _clock.GetElapsedTime(_lastLimitChangeTimestamp, now)));

            if (decision.TargetConcurrency == decision.PreviousConcurrency)
                return;

            _limiter.UpdateLimit(decision.TargetConcurrency);
            _lastLimitChangeTimestamp = now;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;

            _completed = true;
            _limiter.Complete();
        }
    }

    public void Dispose() => Complete();

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
