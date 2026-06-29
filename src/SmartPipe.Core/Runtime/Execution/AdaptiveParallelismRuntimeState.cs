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

    /// <summary>
    /// Initializes a new adaptive parallelism runtime state.
    /// </summary>
    /// <param name="options">The pipeline runtime options that define the adaptive concurrency settings.</param>
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

    /// <summary>
        /// Acquires a concurrency lease.
        /// </summary>
        /// <param name="ct">A cancellation token that cancels the lease acquisition.</param>
        /// <returns>A lease for the current concurrency limit.</returns>
        public ValueTask<AdaptiveConcurrencyLimiter.Lease> AcquireAsync(CancellationToken ct) =>
        _limiter.AcquireAsync(ct);

    /// <summary>
    /// Records the completion of an operation and updates the concurrency limit when needed.
    /// </summary>
    /// <param name="latency">The elapsed time for the completed operation.</param>
    /// <param name="failed">Whether the operation failed.</param>
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
                _clock.GetElapsedTime(_lastLimitChangeTimestamp, now)));

            if (decision.TargetConcurrency == decision.PreviousConcurrency)
                return;

            _limiter.UpdateLimit(decision.TargetConcurrency);
            _lastLimitChangeTimestamp = now;
        }
    }

    /// <summary>
    /// Marks the runtime state as completed.
    /// </summary>
    /// <remarks>
    /// Subsequent completion updates are ignored, and the concurrency limiter is completed.
    /// </remarks>
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

    /// <summary>
/// Completes the runtime state and releases its resources.
/// </summary>
public void Dispose() => Complete();

    /// <summary>
    /// Releases the runtime state.
    /// </summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
