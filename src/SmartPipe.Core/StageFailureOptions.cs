#nullable enable

namespace SmartPipe.Core;

/// <summary>Action applied when a stage reaches a terminal failure.</summary>
public enum FailureAction
{
    /// <summary>Emit a failed processing result and continue according to pipeline policy.</summary>
    EmitFailureResult,

    /// <summary>Route the item to a configured dead-letter sink.</summary>
    DeadLetter,

    /// <summary>Skip the item and continue processing later items.</summary>
    Skip,

    /// <summary>Stop accepting new work and complete the pipeline gracefully.</summary>
    StopPipeline,

    /// <summary>Fault the pipeline run.</summary>
    FaultPipeline,
}

/// <summary>Defines how a retry queue behaves when full.</summary>
public enum RetryQueueOverflowPolicy
{
    /// <summary>Wait until capacity is available.</summary>
    Wait,

    /// <summary>Fail the enqueue operation immediately.</summary>
    FailFast,

    /// <summary>Route the item to dead-letter handling.</summary>
    DeadLetter,

    /// <summary>Drop the newest retry item.</summary>
    DropNewest,

    /// <summary>Drop the oldest retry item.</summary>
    DropOldest,
}

/// <summary>Evaluation model used by a stage circuit breaker.</summary>
public enum CircuitBreakerEvaluationMode
{
    /// <summary>Use the existing compatibility threshold behavior.</summary>
    CompatibilityThreshold,

    /// <summary>Use failure-ratio evaluation over a sampling window.</summary>
    FailureRatio,
}

/// <summary>Configures timeout behavior for a pipeline stage.</summary>
public sealed class TimeoutPolicy
{
    /// <summary>Gets the timeout for one attempt.</summary>
    public TimeSpan? AttemptTimeout { get; init; }

    /// <summary>Gets the timeout for the whole stage, including retries.</summary>
    public TimeSpan? StageTimeout { get; init; }
}

/// <summary>Configures circuit-breaker behavior for a pipeline stage.</summary>
public sealed class CircuitBreakerPolicy
{
    /// <summary>Gets the number of failures allowed before the breaker can open.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Gets the cooldown duration before probing recovery.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the circuit breaker evaluation mode.</summary>
    public CircuitBreakerEvaluationMode EvaluationMode { get; init; } =
        CircuitBreakerEvaluationMode.CompatibilityThreshold;

    /// <summary>Gets the failure ratio threshold for ratio mode.</summary>
    public double FailureRatio { get; init; } = 0.1;

    /// <summary>Gets the sampling window for ratio mode.</summary>
    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the minimum number of samples required before ratio evaluation can open the breaker.</summary>
    public int MinimumThroughput { get; init; } = 100;

    /// <summary>Gets the maximum number of concurrent half-open probes in ratio mode.</summary>
    public int MaxHalfOpenRequests { get; init; } = 1;

    internal void Validate()
    {
        if (FailureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(FailureThreshold), FailureThreshold, "Failure threshold must be greater than zero.");

        if (BreakDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BreakDuration), BreakDuration, "Break duration must be greater than zero.");

        if (!Enum.IsDefined(EvaluationMode))
            throw new ArgumentOutOfRangeException(nameof(EvaluationMode), EvaluationMode, "Circuit breaker evaluation mode is invalid.");

        if (FailureRatio <= 0 || FailureRatio > 1 || double.IsNaN(FailureRatio))
            throw new ArgumentOutOfRangeException(nameof(FailureRatio), FailureRatio, "Failure ratio must be greater than zero and less than or equal to one.");

        if (SamplingDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SamplingDuration), SamplingDuration, "Sampling duration must be greater than zero.");

        if (MinimumThroughput <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumThroughput), MinimumThroughput, "Minimum throughput must be greater than zero.");

        if (MaxHalfOpenRequests <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxHalfOpenRequests), MaxHalfOpenRequests, "Maximum half-open requests must be greater than zero.");
    }
}

/// <summary>Configures retry, timeout, circuit breaker, and terminal failure behavior for a stage.</summary>
public sealed class StageFailureOptions
{
    /// <summary>Gets default stage failure options.</summary>
    public static StageFailureOptions Default { get; } = new();

    /// <summary>Gets retry behavior for transient stage failures.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>Gets timeout behavior for the stage.</summary>
    public TimeoutPolicy? Timeout { get; init; }

    /// <summary>Gets circuit-breaker behavior for the stage.</summary>
    public CircuitBreakerPolicy? CircuitBreaker { get; init; }

    /// <summary>Gets the action for permanent failures.</summary>
    public FailureAction OnPermanentFailure { get; init; } = FailureAction.EmitFailureResult;

    /// <summary>Gets the action for retry exhaustion.</summary>
    public FailureAction OnRetryExhausted { get; init; } = FailureAction.EmitFailureResult;
}
