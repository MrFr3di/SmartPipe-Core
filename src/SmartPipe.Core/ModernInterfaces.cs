#nullable enable

namespace SmartPipe.Core;

/// <summary>Envelope-aware source API used by the SmartPipe 1.1 runtime model.</summary>
/// <typeparam name="T">Payload type emitted by the source.</typeparam>
/// <remarks>
/// Sources that need lineage, dead-letter replay, retry metadata, and observer integration should
/// implement this interface.
/// </remarks>
public interface IPipelineSource<T> : IAsyncDisposable
{
    /// <summary>Initializes the source before enumeration begins.</summary>
    /// <param name="ct">Cancellation token for initialization.</param>
    /// <returns>A value task representing initialization.</returns>
    ValueTask InitializeAsync(CancellationToken ct = default);

    /// <summary>Reads envelopes from the source.</summary>
    /// <param name="ct">Cancellation token for enumeration.</param>
    /// <returns>Async sequence of processing envelopes.</returns>
    IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(CancellationToken ct = default);
}

/// <summary>Envelope-aware transformer API used by the SmartPipe 1.1 runtime model.</summary>
/// <typeparam name="TInput">Input payload type.</typeparam>
/// <typeparam name="TOutput">Output payload type.</typeparam>
/// <remarks>
/// This is the primary API for transforms that need envelope metadata, lineage, and retry attempt
/// information.
/// </remarks>
public interface IPipelineTransformer<TInput, TOutput> : IAsyncDisposable
{
    /// <summary>Initializes the transformer before the first item is processed.</summary>
    /// <param name="ct">Cancellation token for initialization.</param>
    /// <returns>A value task representing initialization.</returns>
    ValueTask InitializeAsync(CancellationToken ct = default);

    /// <summary>Transforms one envelope.</summary>
    /// <param name="envelope">Input envelope.</param>
    /// <param name="ct">Cancellation token for the transform attempt.</param>
    /// <returns>A valid stage result. Implementations must use <see cref="StageResult{T}.Success"/> or another factory method.</returns>
    ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default
    );
}

/// <summary>Envelope-aware sink API used by the SmartPipe 1.1 runtime model.</summary>
/// <typeparam name="T">Payload type consumed by the sink.</typeparam>
/// <remarks>
/// Sinks signal write failures by throwing exceptions. The runtime catches sink exceptions and routes
/// them through the configured failure policy.
/// </remarks>
public interface IPipelineSink<T> : IAsyncDisposable
{
    /// <summary>Initializes the sink before the first write.</summary>
    /// <param name="ct">Cancellation token for initialization.</param>
    /// <returns>A value task representing initialization.</returns>
    ValueTask InitializeAsync(CancellationToken ct = default);

    /// <summary>Writes one envelope to the sink.</summary>
    /// <param name="envelope">Envelope to write.</param>
    /// <param name="ct">Cancellation token for the write.</param>
    /// <returns>A value task representing the write operation.</returns>
    ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default);
}
