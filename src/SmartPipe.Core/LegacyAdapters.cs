#nullable enable

using System.Runtime.CompilerServices;

namespace SmartPipe.Core;

/// <summary>Adapts a legacy <see cref="ISource{T}"/> to the envelope-aware source API.</summary>
/// <typeparam name="T">Payload type.</typeparam>
public sealed class LegacySourceAdapter<T> : IPipelineSource<T>
{
    private readonly ISource<T> _inner;
    private readonly string _pipelineId;
    private readonly string _runId;

    /// <summary>Creates a legacy source adapter.</summary>
    /// <param name="inner">Legacy source to wrap.</param>
    /// <param name="pipelineId">Pipeline identifier used for emitted envelopes.</param>
    /// <param name="runId">Run identifier used for emitted envelopes.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
    public LegacySourceAdapter(
        ISource<T> inner,
        string pipelineId = "legacy",
        string runId = "legacy"
    )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pipelineId = pipelineId;
        _runId = runId;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default) =>
        await _inner.InitializeAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await foreach (var context in _inner.ReadAsync(ct).ConfigureAwait(false))
            yield return ProcessingEnvelope<T>.FromContext(context, _pipelineId, _runId);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _inner.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Adapts a legacy <see cref="ITransformer{TInput,TOutput}"/> to the envelope-aware transformer API.</summary>
/// <typeparam name="TInput">Input payload type.</typeparam>
/// <typeparam name="TOutput">Output payload type.</typeparam>
public sealed class LegacyTransformerAdapter<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly ITransformer<TInput, TOutput> _inner;

    /// <summary>Creates a legacy transformer adapter.</summary>
    /// <param name="inner">Legacy transformer to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
    public LegacyTransformerAdapter(ITransformer<TInput, TOutput> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default) =>
        await _inner.InitializeAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var result = await _inner.TransformAsync(envelope.ToContext(), ct).ConfigureAwait(false);
        return StageResult<TOutput>.FromProcessingResult(result);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _inner.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Adapts a legacy <see cref="ISink{T}"/> to the envelope-aware sink API.</summary>
/// <typeparam name="T">Payload type.</typeparam>
public sealed class LegacySinkAdapter<T> : IPipelineSink<T>
{
    private readonly ISink<T> _inner;

    /// <summary>Creates a legacy sink adapter.</summary>
    /// <param name="inner">Legacy sink to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
    public LegacySinkAdapter(ISink<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default) =>
        await _inner.InitializeAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _inner
            .WriteAsync(ProcessingResult<T>.Success(envelope.Payload, envelope.TraceId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _inner.DisposeAsync().ConfigureAwait(false);
}
