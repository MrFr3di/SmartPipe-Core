#nullable enable

using System.Diagnostics.CodeAnalysis;
namespace SmartPipe.Core;

/// <summary>Replay-safe dead-letter record that preserves original payload and runtime context.</summary>
/// <typeparam name="T">Original payload type.</typeparam>
/// <remarks>
/// <see cref="ProcessingResult{T}"/> is not sufficient for replay because failed results do not
/// carry the original payload. Dead-letter sinks should persist this envelope format for new data.
/// </remarks>
public sealed record DeadLetterEnvelope<T>
{
    /// <summary>Gets the dead-letter schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the pipeline identifier.</summary>
    public required string PipelineId { get; init; }

    /// <summary>Gets the run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Gets the item trace identifier.</summary>
    public required ulong TraceId { get; init; }

    /// <summary>Gets the stage identifier where the failure occurred.</summary>
    public required string StageId { get; init; }

    /// <summary>Gets the stage name where the failure occurred.</summary>
    public required string StageName { get; init; }

    /// <summary>Gets the original payload. Null is valid only when <typeparamref name="T"/> permits null values.</summary>
    public required T OriginalPayload { get; init; }

    /// <summary>Gets metadata associated with the failed item.</summary>
    public required MetadataBag Metadata { get; init; }

    /// <summary>Gets the structured error that caused dead-letter routing.</summary>
    public required SmartPipeError Error { get; init; }

    /// <summary>Gets the attempt number that failed.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets the UTC timestamp when the item failed.</summary>
    public required DateTimeOffset FailedAtUtc { get; init; }
}

/// <summary>Redacts sensitive information before dead-letter persistence.</summary>
/// <typeparam name="T">Payload type.</typeparam>
public interface IDeadLetterRedactor<T>
{
    /// <summary>Redacts a dead-letter envelope.</summary>
    /// <param name="envelope">Envelope to redact.</param>
    /// <returns>The redacted envelope.</returns>
    DeadLetterEnvelope<T> Redact(DeadLetterEnvelope<T> envelope);
}

/// <summary>No-op dead-letter redactor.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <remarks>
/// This redactor preserves the full payload. Applications that handle PII, secrets, or regulated
/// data should provide an explicit redactor before enabling dead-letter persistence.
/// </remarks>
public sealed class NoOpDeadLetterRedactor<T> : IDeadLetterRedactor<T>
{
    /// <inheritdoc />
    public DeadLetterEnvelope<T> Redact(DeadLetterEnvelope<T> envelope) => envelope;
}

/// <summary>Configures dead-letter persistence for one typed pipeline stage.</summary>
/// <typeparam name="T">Stage input payload type preserved as the original payload.</typeparam>
/// <remarks>
/// The runtime does not own or dispose <see cref="Stream"/>. Callers decide stream lifetime,
/// storage rotation, encryption, and access control.
/// </remarks>
public sealed class StageDeadLetterOptions<T>
{
    /// <summary>Creates dead-letter options for a stage.</summary>
    /// <param name="stream">Destination stream. The runtime writes JSON Lines records and leaves the stream open.</param>
    /// <param name="serializer">Serializer used to persist envelopes. Uses JSON Lines by default.</param>
    /// <param name="redactor">Redactor applied before persistence. Uses no-op redaction by default.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    [RequiresUnreferencedCode("The default dead-letter serializer uses reflection-based JSON metadata. Pass a source-generated serializer or use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("The default dead-letter serializer may require runtime code generation. Pass a source-generated serializer or use the JsonTypeInfo constructor for NativeAOT.")]
    public StageDeadLetterOptions(
        Stream stream,
        IDeadLetterSerializer<T>? serializer = null,
        IDeadLetterRedactor<T>? redactor = null)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Serializer = serializer ?? new JsonLinesDeadLetterSerializer<T>();
        Redactor = redactor ?? new NoOpDeadLetterRedactor<T>();
    }

    /// <summary>Gets the destination stream.</summary>
    public Stream Stream { get; }

    /// <summary>Gets the serializer used for persistence.</summary>
    public IDeadLetterSerializer<T> Serializer { get; }

    /// <summary>Gets the redactor applied before persistence.</summary>
    public IDeadLetterRedactor<T> Redactor { get; }
}
