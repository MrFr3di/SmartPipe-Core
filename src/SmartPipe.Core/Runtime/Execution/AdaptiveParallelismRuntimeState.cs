#nullable enable

namespace SmartPipe.Core;

internal sealed class AdaptiveParallelismRuntimeState : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly IPipelineClock _clock;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly AdaptiveParallelismController _controller;
    private readonly AdaptiveParallelismOptions _adaptiveOptions;
    private long _lastAdjustmentTimestamp;
    private long _lastEvaluationTimestamp;
    private long _intervalProcessed;
    private long _intervalFailed;
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
            EvaluationInterval = adaptive.EvaluationInterval,
            AdjustmentCooldown = adaptive.AdjustmentCooldown,
            MaxAdjustmentStep = adaptive.MaxAdjustmentStep,
            FailurePressureThreshold = adaptive.FailurePressureThreshold,
            MinimumFailureSamples = adaptive.MinimumFailureSamples,
            MinSmoothingFactor = adaptive.MinSmoothingFactor,
        };

        _clock = options.Clock;
        _adaptiveOptions = controllerOptions;
        _limiter = new AdaptiveConcurrencyLimiter(initialLimit, effectiveAdaptiveMax);
        _controller = new AdaptiveParallelismController(controllerOptions);
        var now = _clock.GetTimestamp();
        _lastAdjustmentTimestamp = now;
        _lastEvaluationTimestamp = now;
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

            _intervalProcessed++;
            if (failed)
                _intervalFailed++;

            var now = _clock.GetTimestamp();
            var sinceEvaluation = _clock.GetElapsedTime(_lastEvaluationTimestamp, now);
            if (sinceEvaluation < _adaptiveOptions.EvaluationInterval)
                return;

            var processed = _intervalProcessed;
            var failedCount = _intervalFailed;
            _intervalProcessed = 0;
            _intervalFailed = 0;
            _lastEvaluationTimestamp = now;

            var decision = _controller.Decide(new AdaptiveParallelismSnapshot(
                _limiter.CurrentLimit,
                latency,
                ProcessedDelta: processed,
                FailedDelta: failedCount,
                _clock.GetElapsedTime(_lastAdjustmentTimestamp, now)));

            if (decision.TargetConcurrency == decision.PreviousConcurrency)
                return;

            _limiter.UpdateLimit(decision.TargetConcurrency);
            _lastAdjustmentTimestamp = now;
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
