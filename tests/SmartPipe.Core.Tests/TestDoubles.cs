using SmartPipe.Core;

namespace SmartPipe.Core.Tests;

internal class SimpleSource<T> : ISource<T>
{
    private readonly T[] _items;
    public SimpleSource(params T[] items) => _items = items;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ProcessingContext<T>(item);
            await Task.Yield();
        }
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class PassthroughTransformer<T> : ITransformer<T, T>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
        => ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class CollectionSink<T> : ISink<T>
{
    private readonly List<T> _results = new();
    public IReadOnlyList<T> Results => _results;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            lock (_results) _results.Add(result.Value);
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class SimpleTransformer<T> : ITransformer<T, T>
{
    private readonly double _failureRate;
    private int _count;
    private readonly Random _rng = new(42);
    public SimpleTransformer(double failureRate = 0) => _failureRate = failureRate;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        int current = Interlocked.Increment(ref _count);
        if (_failureRate > 0 && _rng.NextDouble() < _failureRate)
            return ValueTask.FromResult(ProcessingResult<T>.Failure(
                new SmartPipeError($"Failure #{current}", ErrorType.Transient), ctx.TraceId));
        return ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));
    }
    public Task DisposeAsync() => Task.CompletedTask;
}
