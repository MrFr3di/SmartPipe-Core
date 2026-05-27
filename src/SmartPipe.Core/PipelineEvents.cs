#nullable enable

namespace SmartPipe.Core;

/// <summary>Reliability category for a pipeline observer.</summary>
public enum ObserverReliability
{
    /// <summary>Best-effort diagnostics such as routine logging or metrics.</summary>
    BestEffort,

    /// <summary>Reliable audit observers such as dead-letter or lineage capture.</summary>
    Reliable,

    /// <summary>Critical policy observers that may fault the pipeline.</summary>
    Critical,
}

/// <summary>Policy for observer failures.</summary>
public enum ObserverFailurePolicy
{
    /// <summary>Ignore observer failures.</summary>
    Ignore,

    /// <summary>Log observer failures when a logger is available.</summary>
    Log,

    /// <summary>Fault the pipeline when an observer fails.</summary>
    FaultPipeline,

    /// <summary>Remove the failing observer from later dispatch.</summary>
    RemoveObserver,
}

/// <summary>Policy for a full observer event queue.</summary>
public enum ObserverQueueOverflowPolicy
{
    /// <summary>Wait until queue capacity is available.</summary>
    Wait,

    /// <summary>Drop the newest event.</summary>
    DropNewest,

    /// <summary>Drop the oldest queued event.</summary>
    DropOldest,

    /// <summary>Fault the pipeline when observer events cannot be queued.</summary>
    FaultPipeline,
}

/// <summary>Receives pipeline events.</summary>
public interface IPipelineObserver
{
    /// <summary>Handles one pipeline event.</summary>
    /// <param name="pipelineEvent">Event to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A value task representing observer work.</returns>
    ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default);
}

/// <summary>Configuration for one pipeline observer registration.</summary>
/// <param name="Observer">Observer instance.</param>
/// <param name="Reliability">Observer reliability category.</param>
/// <param name="FailurePolicy">Policy used when the observer throws.</param>
public sealed record PipelineObserverRegistration(
    IPipelineObserver Observer,
    ObserverReliability Reliability = ObserverReliability.BestEffort,
    ObserverFailurePolicy FailurePolicy = ObserverFailurePolicy.Log
);

/// <summary>Base event with correlation fields for pipeline observability.</summary>
/// <param name="PipelineId">Pipeline identifier.</param>
/// <param name="RunId">Run identifier.</param>
/// <param name="TraceId">Item trace identifier, or zero for run-level events.</param>
/// <param name="StageId">Stage identifier for stage events.</param>
/// <param name="Attempt">Attempt number.</param>
/// <param name="TimestampUtc">UTC timestamp.</param>
public abstract record PipelineEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string? StageId,
    int Attempt,
    DateTimeOffset TimestampUtc
);

/// <summary>Event emitted when a pipeline run starts.</summary>
public sealed record PipelineStartedEvent(
    string PipelineId,
    string RunId,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, 0, null, 0, TimestampUtc);

/// <summary>Event emitted when a pipeline run completes.</summary>
public sealed record PipelineCompletedEvent(
    string PipelineId,
    string RunId,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, 0, null, 0, TimestampUtc);

/// <summary>Event emitted when a pipeline run faults.</summary>
public sealed record PipelineFaultedEvent(
    string PipelineId,
    string RunId,
    DateTimeOffset TimestampUtc,
    Exception Exception
) : PipelineEvent(PipelineId, RunId, 0, null, 0, TimestampUtc);

/// <summary>Event emitted when a pipeline run is cancelled.</summary>
public sealed record PipelineCancelledEvent(
    string PipelineId,
    string RunId,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, 0, null, 0, TimestampUtc);

/// <summary>Event emitted before a stage is invoked.</summary>
public sealed record StageStartedEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    string StageName,
    int Attempt,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted after a stage succeeds.</summary>
public sealed record StageSucceededEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    string StageName,
    int Attempt,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted when a stage fails.</summary>
public sealed record StageFailedEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    int Attempt,
    DateTimeOffset TimestampUtc,
    SmartPipeError Error
) : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted when a retry is scheduled for a failed stage attempt.</summary>
public sealed record RetryScheduledEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    int Attempt,
    DateTimeOffset TimestampUtc,
    TimeSpan Delay,
    SmartPipeError Error
) : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted immediately before a retry attempt is invoked.</summary>
public sealed record RetryAttemptedEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    int Attempt,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted when retry budget is exhausted for a stage item.</summary>
public sealed record RetryExhaustedEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    int Attempt,
    DateTimeOffset TimestampUtc,
    SmartPipeError Error
) : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted before a sink writes an envelope.</summary>
public sealed record SinkWriteStartedEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    int Attempt,
    DateTimeOffset TimestampUtc
) : PipelineEvent(PipelineId, RunId, TraceId, null, Attempt, TimestampUtc);

/// <summary>Event emitted when a sink write fails.</summary>
public sealed record SinkWriteFailedEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    int Attempt,
    DateTimeOffset TimestampUtc,
    Exception Exception
) : PipelineEvent(PipelineId, RunId, TraceId, null, Attempt, TimestampUtc);

/// <summary>Event emitted after an item is written to dead-letter storage.</summary>
public sealed record DeadLetterWrittenEvent(
    string PipelineId,
    string RunId,
    ulong TraceId,
    string StageId,
    string StageName,
    int Attempt,
    DateTimeOffset TimestampUtc)
    : PipelineEvent(PipelineId, RunId, TraceId, StageId, Attempt, TimestampUtc);

/// <summary>Event emitted when an observer fails.</summary>
public sealed record ObserverFailedEvent(
    string PipelineId,
    string RunId,
    string ObserverName,
    DateTimeOffset TimestampUtc,
    Exception Exception
) : PipelineEvent(PipelineId, RunId, 0, null, 0, TimestampUtc);
