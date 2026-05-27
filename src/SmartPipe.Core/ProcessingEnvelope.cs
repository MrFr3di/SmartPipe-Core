#nullable enable

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

    /// <summary>Creates a runtime envelope from a legacy processing context.</summary>
    /// <param name="context">Legacy context to adapt.</param>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <param name="runId">Run identifier.</param>
    /// <returns>A new processing envelope.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    public static ProcessingEnvelope<T> FromContext(
        ProcessingContext<T> context,
        string pipelineId = "legacy",
        string runId = "legacy"
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ProcessingEnvelope<T>
        {
            PipelineId = pipelineId,
            RunId = runId,
            TraceId = context.TraceId,
            Payload = context.Payload,
            Metadata = MetadataBag.From(context.Metadata),
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Creates a legacy processing context from this envelope.</summary>
    /// <returns>A new legacy context preserving payload, metadata, and trace identifier.</returns>
    public ProcessingContext<T> ToContext()
    {
        return new ProcessingContext<T>(Payload, Metadata.ToDictionary()) { TraceId = TraceId };
    }
}
