#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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

internal class AcceptedTrackingSource<T> : ISource<T>
{
    private readonly T[] _items;
    private int _acceptedCount;

    public AcceptedTrackingSource(params T[] items) => _items = items;

    public int AcceptedCount => Volatile.Read(ref _acceptedCount);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ProcessingContext<T>(item);
            Interlocked.Increment(ref _acceptedCount);
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
    private readonly List<T> _results = [];
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

internal class IdentityTransformer<T> : ITransformer<T, T>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
        => ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class CallbackSink<T> : ISink<T>
{
    private readonly Action<T> _callback;
    public CallbackSink(Action<T> callback) => _callback = callback;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            _callback(result.Value);
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class NoOpSink<T> : ISink<T>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class DisposableCountingSource<T> : ISource<T>
{
    private readonly T[] _items;
    private int _disposeCallCount;
    public int DisposeCallCount => _disposeCallCount;
    public DisposableCountingSource(params T[] items) => _items = items;
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
    public Task DisposeAsync() { Interlocked.Increment(ref _disposeCallCount); return Task.CompletedTask; }
}

internal class DisposableCountingTransformer<TIn, TOut> : ITransformer<TIn, TOut>
{
    private int _disposeCallCount;
    public int DisposeCallCount => _disposeCallCount;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<TOut>> TransformAsync(ProcessingContext<TIn> ctx, CancellationToken ct = default)
        => ValueTask.FromResult(ProcessingResult<TOut>.Success((TOut)(object)ctx.Payload!, ctx.TraceId));
    public Task DisposeAsync() { Interlocked.Increment(ref _disposeCallCount); return Task.CompletedTask; }
}

internal class DisposableCountingSink<T> : ISink<T>
{
    private int _disposeCallCount;
    public int DisposeCallCount => _disposeCallCount;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisposeAsync() { Interlocked.Increment(ref _disposeCallCount); return Task.CompletedTask; }
}

internal class InfiniteSource<T> : ISource<T>
{
    private int _count;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10, ct);
            yield return new ProcessingContext<T>((T)(object)Interlocked.Increment(ref _count));
        }
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class ThrowingInitializeSource<T> : ISource<T>
{
    public Task InitializeAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("source initialization failed");

    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

internal class AlwaysTransientFailingTransformer<TIn, TOut> : ITransformer<TIn, TOut>
{
    public int CallCount;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<TOut>> TransformAsync(ProcessingContext<TIn> ctx, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        return ValueTask.FromResult(ProcessingResult<TOut>.Failure(
            new SmartPipeError($"Transient failure #{CallCount}", ErrorType.Transient, "Test"),
            ctx.TraceId));
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}

internal class CollectingDeadLetterSink : ISink<object>
{
    private readonly ConcurrentQueue<ProcessingResult<object>> _items = new();
    public IReadOnlyCollection<ProcessingResult<object>> Items => _items;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<object> result, CancellationToken ct = default)
    {
        _items.Enqueue(result);
        return Task.CompletedTask;
    }
    public int Count => _items.Count;
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class CollectingSink<T> : ISink<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    public IReadOnlyCollection<T> Items => _items;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            _items.Enqueue(result.Value);
        return Task.CompletedTask;
    }
    public int Count => _items.Count;
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class DelayedRetryPolicy : RetryPolicy
{
    private readonly TimeSpan _delay;
    public DelayedRetryPolicy(int maxRetries, TimeSpan delay)
        : base(maxRetries, delay) { _delay = delay; }
}

internal class SlowTransformer<T> : ITransformer<T, T>
{
    private readonly TimeSpan _delay;
    public SlowTransformer(TimeSpan delay) => _delay = delay;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        await Task.Delay(_delay, ct);
        return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class RetrySucceedingTransformer<T> : ITransformer<T, T>
{
    private int _callCount;
    public int CallCount => _callCount;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        int call = Interlocked.Increment(ref _callCount);
        if (call == 1)
            return ValueTask.FromResult(ProcessingResult<T>.Failure(
                new SmartPipeError("Transient failure – will succeed on retry", ErrorType.Transient, "Test"),
                ctx.TraceId));
        return ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal static class TinyCapacityOptionsFactory
{
    public static SmartPipeChannelOptions Create(int capacity = 1) => new()
    {
        BoundedCapacity = capacity,
        MaxDegreeOfParallelism = 1,
    };
}
