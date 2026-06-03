#nullable enable

using System.Threading;

namespace SmartPipe.Core;

/// <summary>Runtime envelope that carries an item and its execution metadata through a pipeline.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <remarks>
/// <see cref="ProcessingEnvelope{T}"/> is the primary runtime model for SmartPipe 1.1.0
/// advanced APIs. Legacy <see cref="ProcessingContext{T}"/> instances are adapted into
/// envelopes by compatibility adapters. Null payloads are valid only when <typeparamref name="T"/>
/// permits null values; transforms should not assume non-null payloads unless the generic type
/// contract does so.
/// </remarks>
public sealed record ProcessingEnvelope<T>
{
    private static ulong _nextTraceId;

    /// <summary>Pipeline identifier that produced this envelope.</summary>
    public required string PipelineId { get; init; }

    /// <summary>Runtime run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Trace identifier preserved across stages, retries, and dead-letter routing.</summary>
    public required ulong TraceId { get; init; }

    /// <summary>Original or current payload carried by the envelope.</summary>
    public required T Payload { get; init; }

    /// <summary>Immutable metadata associated with the item.</summary>
    public required MetadataBag Metadata { get; init; }

    /// <summary>Lineage entries recorded according to the configured <see cref="LineageMode"/>.</summary>
    public required IReadOnlyList<LineageEntry> Lineage { get; init; }

    /// <summary>Current retry attempt number for this item.</summary>
    public required int Attempt { get; init; }

    /// <summary>UTC timestamp when this envelope was created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Creates an envelope with default runtime metadata.</summary>
    /// <param name="payload">Payload to carry.</param>
    /// <returns>A new processing envelope.</returns>
    public static ProcessingEnvelope<T> Create(T payload)
    {
        return Create(
            payload,
            "default",
            Guid.NewGuid().ToString("N"),
            NextTraceId(),
            MetadataBag.Empty,
            SystemPipelineClock.Instance.GetUtcNow()
        );
    }

    /// <summary>Creates an envelope with explicit correlation values.</summary>
    /// <param name="payload">Payload to carry.</param>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <param name="runId">Run identifier.</param>
    /// <param name="traceId">Trace identifier.</param>
    /// <param name="metadata">Optional metadata. Empty metadata is used when null.</param>
    /// <param name="createdAtUtc">Optional created timestamp. The system pipeline clock is used when null.</param>
    /// <returns>A new processing envelope.</returns>
    /// <exception cref="ArgumentException">Thrown when pipelineId or runId is empty.</exception>
    public static ProcessingEnvelope<T> Create(
        T payload,
        string pipelineId,
        string runId,
        ulong traceId,
        MetadataBag? metadata = null,
        DateTimeOffset? createdAtUtc = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return new ProcessingEnvelope<T>
        {
            PipelineId = pipelineId,
            RunId = runId,
            TraceId = traceId,
            Payload = payload,
            Metadata = metadata ?? MetadataBag.Empty,
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = createdAtUtc ?? SystemPipelineClock.Instance.GetUtcNow(),
        };
    }

    private static ulong NextTraceId()
    {
        var next = Interlocked.Increment(ref _nextTraceId);
        return unchecked((ulong)next);
    }

    /// <summary>Creates a runtime envelope from a legacy processing context.</summary>
    /// <param name="context">Legacy context to adapt.</param>
    /// <returns>A new processing envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    public static ProcessingEnvelope<T> FromContext(ProcessingContext<T> context)
    {
        return FromContext(context, "legacy", "legacy");
    }

    /// <summary>Creates a runtime envelope from a legacy processing context.</summary>
    /// <param name="context">Legacy context to adapt.</param>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <returns>A new processing envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    public static ProcessingEnvelope<T> FromContext(
        ProcessingContext<T> context,
        string pipelineId
    )
    {
        return FromContext(context, pipelineId, "legacy");
    }

    /// <summary>Creates a runtime envelope from a legacy processing context.</summary>
    /// <param name="context">Legacy context to adapt.</param>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <param name="runId">Run identifier.</param>
    /// <returns>A new processing envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027",
        Justification = "Existing shipped overload keeps optional parameters for source compatibility."
    )]
    public static ProcessingEnvelope<T> FromContext(
        ProcessingContext<T> context,
        string pipelineId = "legacy",
        string runId = "legacy"
    )
    {
        return FromContext(context, SystemPipelineClock.Instance, pipelineId, runId);
    }

    /// <summary>Creates a runtime envelope from a legacy processing context.</summary>
    /// <param name="context">Legacy context to adapt.</param>
    /// <param name="clock">Clock used for the created timestamp.</param>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <param name="runId">Run identifier.</param>
    /// <returns>A new processing envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> or <paramref name="clock"/> is null.</exception>
    public static ProcessingEnvelope<T> FromContext(
        ProcessingContext<T> context,
        IPipelineClock clock,
        string pipelineId,
        string runId
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        return new ProcessingEnvelope<T>
        {
            PipelineId = pipelineId,
            RunId = runId,
            TraceId = context.TraceId,
            Payload = context.Payload,
            Metadata = MetadataBag.From(context.Metadata),
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = clock.GetUtcNow(),
        };
    }

    /// <summary>Creates a legacy processing context from this envelope.</summary>
    /// <returns>A new legacy context preserving payload, metadata, and trace identifier.</returns>
    public ProcessingContext<T> ToContext()
    {
        return new ProcessingContext<T>(Payload, Metadata.ToDictionary()) { TraceId = TraceId };
    }
}
