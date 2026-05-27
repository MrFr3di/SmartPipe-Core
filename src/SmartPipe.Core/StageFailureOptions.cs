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
