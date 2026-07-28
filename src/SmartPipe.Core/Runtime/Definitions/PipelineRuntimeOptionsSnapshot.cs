#nullable enable
#pragma warning disable CS0618 // Compatibility aliases are copied deliberately.

using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed record PipelineRuntimeOptionsSnapshot(
    int MaxConcurrency,
    int InputCapacity,
    BoundedChannelFullMode InputFullMode,
    int? OutputCapacity,
    BoundedChannelFullMode OutputFullMode,
    PipelineOutputMode OutputMode,
    bool IsOutputModeConfigured,
    int MaxDegreeOfParallelism,
    PipelineOutputPolicy OutputPolicy,
    bool IsOutputPolicyConfigured,
    PipelineOrderingMode OrderingMode,
    ObserverDispatchOptionsSnapshot ObserverDispatch,
    AdaptiveParallelismOptionsSnapshot AdaptiveParallelism,
    IPipelineClock Clock,
    bool IsClockConfigured)
{
    public static PipelineRuntimeOptionsSnapshot Create(PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return new(
            options.MaxConcurrency,
            options.InputCapacity,
            options.InputFullMode,
            options.OutputCapacity,
            options.OutputFullMode,
            options.OutputMode,
            options.IsOutputModeConfigured,
            options.MaxDegreeOfParallelism,
            options.OutputPolicy,
            options.IsOutputPolicyConfigured,
            options.OrderingMode,
            ObserverDispatchOptionsSnapshot.Create(options.ObserverDispatch),
            AdaptiveParallelismOptionsSnapshot.Create(options.AdaptiveParallelism),
            options.Clock,
            options.IsClockConfigured);
    }

    public bool UseCompatibilityOutputMode =>
        IsOutputModeConfigured && !IsOutputPolicyConfigured;

    public int EffectiveMaxConcurrency =>
        MaxConcurrency != 1 ? MaxConcurrency : MaxDegreeOfParallelism;

    public IPipelineClock ResolveClock(PipelineActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HasExplicitTimeProvider)
            return new TimeProviderPipelineClock(context.TimeProvider);

        return IsClockConfigured ? Clock : SystemPipelineClock.Instance;
    }

    public PipelineRuntimeOptions Materialize() => new(this);

    public PipelineRuntimeOptions Materialize(PipelineActivationContext context) =>
        new(this, ResolveClock(context));

    public void Validate() => Materialize().Validate();
}

internal sealed record ObserverDispatchOptionsSnapshot(
    ObserverDispatchMode Mode,
    int Capacity,
    BoundedChannelFullMode FullMode,
    ObserverFailureMode FailureMode,
    bool FlushOnCompletion,
    TimeSpan BestEffortWriteTimeout,
    bool EmitDroppedObserverEvents)
{
    public static ObserverDispatchOptionsSnapshot Create(ObserverDispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return new(
            options.Mode,
            options.Capacity,
            options.FullMode,
            options.FailureMode,
            options.FlushOnCompletion,
            options.BestEffortWriteTimeout,
            options.EmitDroppedObserverEvents);
    }

    public ObserverDispatchOptions Materialize() =>
        new()
        {
            Mode = Mode,
            Capacity = Capacity,
            FullMode = FullMode,
            FailureMode = FailureMode,
            FlushOnCompletion = FlushOnCompletion,
            BestEffortWriteTimeout = BestEffortWriteTimeout,
            EmitDroppedObserverEvents = EmitDroppedObserverEvents,
        };
}

internal sealed record AdaptiveParallelismOptionsSnapshot(
    bool Enabled,
    int MinConcurrency,
    int MaxConcurrency,
    int InitialConcurrency,
    TimeSpan TargetLatency,
    TimeSpan DeadZone,
    TimeSpan EvaluationInterval,
    TimeSpan AdjustmentCooldown,
    int MaxAdjustmentStep,
    double FailurePressureThreshold,
    int MinimumFailureSamples,
    double MinSmoothingFactor)
{
    public TimeSpan SampleInterval => EvaluationInterval;

    public TimeSpan Cooldown => AdjustmentCooldown;

    public static AdaptiveParallelismOptionsSnapshot Create(AdaptiveParallelismOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return new(
            options.Enabled,
            options.MinConcurrency,
            options.MaxConcurrency,
            options.InitialConcurrency,
            options.TargetLatency,
            options.DeadZone,
            options.EvaluationInterval,
            options.AdjustmentCooldown,
            options.MaxAdjustmentStep,
            options.FailurePressureThreshold,
            options.MinimumFailureSamples,
            options.MinSmoothingFactor);
    }

    public AdaptiveParallelismOptions Materialize() =>
        new()
        {
            Enabled = Enabled,
            MinConcurrency = MinConcurrency,
            MaxConcurrency = MaxConcurrency,
            InitialConcurrency = InitialConcurrency,
            TargetLatency = TargetLatency,
            DeadZone = DeadZone,
            EvaluationInterval = EvaluationInterval,
            AdjustmentCooldown = AdjustmentCooldown,
            MaxAdjustmentStep = MaxAdjustmentStep,
            FailurePressureThreshold = FailurePressureThreshold,
            MinimumFailureSamples = MinimumFailureSamples,
            MinSmoothingFactor = MinSmoothingFactor,
        };
}

internal sealed record StageFailureOptionsSnapshot(
    RetryPolicy? Retry,
    TimeoutPolicy? Timeout,
    CircuitBreakerPolicy? CircuitBreaker,
    Func<Exception, SmartPipeError>? ExceptionClassifier,
    FailureAction OnPermanentFailure,
    FailureAction OnRetryExhausted)
{
    public static StageFailureOptionsSnapshot Create(StageFailureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CircuitBreaker?.Validate();

        return new(
            CopyRetry(options.Retry),
            CopyTimeout(options.Timeout),
            CopyCircuitBreaker(options.CircuitBreaker),
            options.ExceptionClassifier,
            options.OnPermanentFailure,
            options.OnRetryExhausted);
    }

    public StageFailureOptions Materialize() =>
        new()
        {
            Retry = CopyRetry(Retry),
            Timeout = CopyTimeout(Timeout),
            CircuitBreaker = CopyCircuitBreaker(CircuitBreaker),
            ExceptionClassifier = ExceptionClassifier,
            OnPermanentFailure = OnPermanentFailure,
            OnRetryExhausted = OnRetryExhausted,
        };

    public void Validate()
    {
        if (!Enum.IsDefined(OnPermanentFailure))
        {
            throw new ArgumentOutOfRangeException(
                nameof(OnPermanentFailure),
                OnPermanentFailure,
                "Permanent failure action is invalid.");
        }

        if (!Enum.IsDefined(OnRetryExhausted))
        {
            throw new ArgumentOutOfRangeException(
                nameof(OnRetryExhausted),
                OnRetryExhausted,
                "Retry exhausted action is invalid.");
        }

        CircuitBreaker?.Validate();
    }

    private static RetryPolicy? CopyRetry(RetryPolicy? policy) =>
        policy is null
            ? null
            : new RetryPolicy(
                policy.MaxRetries,
                policy.Delay,
                policy.MaxDelay,
                policy.Strategy,
                policy.RetryOn,
                policy.OnRetry);

    private static TimeoutPolicy? CopyTimeout(TimeoutPolicy? policy) =>
        policy is null
            ? null
            : new TimeoutPolicy
            {
                AttemptTimeout = policy.AttemptTimeout,
                StageTimeout = policy.StageTimeout,
                RetryMode = policy.RetryMode,
                CancellationGracePeriod = policy.CancellationGracePeriod,
                LateAttemptFinalizationTimeout = policy.LateAttemptFinalizationTimeout,
            };

    private static CircuitBreakerPolicy? CopyCircuitBreaker(CircuitBreakerPolicy? policy) =>
        policy is null
            ? null
            : new CircuitBreakerPolicy
            {
                FailureThreshold = policy.FailureThreshold,
                BreakDuration = policy.BreakDuration,
                EvaluationMode = policy.EvaluationMode,
                FailureRatio = policy.FailureRatio,
                SamplingDuration = policy.SamplingDuration,
                MinimumThroughput = policy.MinimumThroughput,
                MaxHalfOpenRequests = policy.MaxHalfOpenRequests,
            };
}
