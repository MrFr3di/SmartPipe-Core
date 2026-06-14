#nullable enable

using System.Runtime.CompilerServices;

namespace SmartPipe.Core;

/// <summary>Factory methods for lightweight typed pipeline sources.</summary>
public static class PipelineSource
{
    /// <summary>Creates a typed source from an async enumerable with default identifiers.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="items">Items to emit.</param>
    /// <returns>A typed pipeline source.</returns>
    public static IPipelineSource<T> FromAsyncEnumerable<T>(IAsyncEnumerable<T> items) =>
        FromAsyncEnumerable(items, "pipeline", "run");

    /// <summary>Creates a typed source from an async enumerable.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="items">Items to emit.</param>
    /// <param name="pipelineId">Pipeline identifier assigned to emitted envelopes.</param>
    /// <param name="runId">Run identifier assigned to emitted envelopes.</param>
    /// <returns>A typed pipeline source.</returns>
    public static IPipelineSource<T> FromAsyncEnumerable<T>(
        IAsyncEnumerable<T> items,
        string pipelineId,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return new AsyncEnumerablePipelineSource<T>(items, pipelineId, runId);
    }
}

/// <summary>Factory methods for lightweight typed pipeline transformers.</summary>
public static class PipelineTransformer
{
    /// <summary>Creates a typed transformer from a payload function.</summary>
    /// <typeparam name="TInput">Input payload type.</typeparam>
    /// <typeparam name="TOutput">Output payload type.</typeparam>
    /// <param name="transform">Transformation function.</param>
    /// <returns>A typed pipeline transformer.</returns>
    public static IPipelineTransformer<TInput, TOutput> FromFunc<TInput, TOutput>(
        Func<TInput, CancellationToken, ValueTask<TOutput>> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new FuncPipelineTransformer<TInput, TOutput>(transform);
    }
}

/// <summary>Factory methods for lightweight typed pipeline sinks.</summary>
public static class PipelineSink
{
    /// <summary>Creates a typed sink from a payload write function.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="write">Write function.</param>
    /// <returns>A typed pipeline sink.</returns>
    public static IPipelineSink<T> FromFunc<T>(
        Func<T, CancellationToken, ValueTask> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return new FuncPipelineSink<T>(write);
    }
}

internal sealed class AsyncEnumerablePipelineSource<T> : IPipelineSource<T>
{
    private readonly IAsyncEnumerable<T> _items;
    private readonly string _pipelineId;
    private readonly string _runId;
    private long _nextTraceId;

    public AsyncEnumerablePipelineSource(
        IAsyncEnumerable<T> items,
        string pipelineId,
        string runId)
    {
        _items = items;
        _pipelineId = pipelineId;
        _runId = runId;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _items.WithCancellation(ct).ConfigureAwait(false))
        {
            var traceId = unchecked((ulong)Interlocked.Increment(ref _nextTraceId));
            yield return ProcessingEnvelope<T>.Create(item, _pipelineId, _runId, traceId);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FuncPipelineTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly Func<TInput, CancellationToken, ValueTask<TOutput>> _transform;

    public FuncPipelineTransformer(Func<TInput, CancellationToken, ValueTask<TOutput>> transform)
    {
        _transform = transform;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        var output = await _transform(envelope.Payload, ct).ConfigureAwait(false);
        return StageResult<TOutput>.Success(output);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FuncPipelineSink<T> : IPipelineSink<T>
{
    private readonly Func<T, CancellationToken, ValueTask> _write;

    public FuncPipelineSink(Func<T, CancellationToken, ValueTask> write)
    {
        _write = write;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default) =>
        _write(envelope.Payload, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
