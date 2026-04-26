using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests;

public class ChaosEngineeringTests
{
    [Fact]
    public async Task SlowConsumer_ShouldTriggerBackpressure()
    {
        var items = Enumerable.Range(1, 100).ToArray();
        var source = new SimpleSource<int>(items);
        var transformer = new PassthroughTransformer<int>();
        var sink = new ChaosSlowSink<int>(10);
        var options = new SmartPipeChannelOptions { BoundedCapacity = 10, MaxDegreeOfParallelism = 1 };
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Should().HaveCount(100);
    }

    [Fact]
    public async Task FailingSource_ShouldHandleGracefully()
    {
        var source = new ChaosSource<int>(Enumerable.Range(1, 100).ToArray());
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(new SmartPipeChannelOptions { ContinueOnError = true });
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExceptionThrowingTransformer_ShouldBeCaught()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5, 6);
        var transformer = new ChaosTransformer<int>(2);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(new SmartPipeChannelOptions { ContinueOnError = true });
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DropSink_ShouldNotCrashPipeline()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        var transformer = new PassthroughTransformer<int>();
        var sink = new ChaosDropSink<int>(3);
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Count.Should().BeLessThan(10);
        channel.Metrics.ItemsProcessed.Should().Be(10);
    }

    [Fact]
    public async Task CircuitBreaker_ShouldRecoverAfterCooldown()
    {
        var opts = new SmartPipeChannelOptions { ContinueOnError = true };
        opts.EnableFeature("CircuitBreaker");
        var source = new SimpleSource<int>(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        var transformer = new SimpleTransformer<int>(0.9);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(opts);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        await channel.RunAsync();
        channel.Metrics.ItemsFailed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PauseResume_ShouldNotLoseData()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        var runTask = channel.RunAsync();
        await Task.Delay(50);
        channel.Pause();
        await Task.Delay(50);
        channel.Resume();
        await runTask;
        sink.Results.Should().HaveCount(10);
    }

    [Fact]
    public async Task Cancellation_ShouldStopCleanly()
    {
        var items = Enumerable.Range(1, 10000).ToArray();
        var source = new SimpleSource<int>(items);
        var transformer = new ChaosSlowTransformer<int>(1);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        using var cts = new CancellationTokenSource(100);
        await channel.RunAsync(cts.Token);
        sink.Results.Count.Should().BeLessThan(10000);
    }
}

// Chaos-specific test doubles
internal class ChaosSource<T> : ISource<T>
{
    private readonly T[] _items;
    private readonly Random _rng = new(42);
    public ChaosSource(T[] items) => _items = items;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            if (_rng.Next(100) < 20) throw new IOException("Chaos: source failure");
            yield return new ProcessingContext<T>(item);
        }
        await Task.CompletedTask;
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class ChaosTransformer<T> : ITransformer<T, T>
{
    private int _count;
    private readonly int _failEvery;
    public ChaosTransformer(int failEvery = 3) => _failEvery = failEvery;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _count) % _failEvery == 0)
            throw new TimeoutException("Chaos timeout");
        return ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId));
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class ChaosSlowSink<T> : ISink<T>
{
    private readonly int _delayMs;
    private readonly List<T> _results = new();
    public IReadOnlyList<T> Results => _results;
    public ChaosSlowSink(int delayMs = 50) => _delayMs = delayMs;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        await Task.Delay(_delayMs, ct);
        if (result.IsSuccess && result.Value != null) lock (_results) _results.Add(result.Value);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class ChaosDropSink<T> : ISink<T>
{
    private int _count;
    private readonly int _dropEvery;
    private readonly List<T> _results = new();
    public IReadOnlyList<T> Results => _results;
    public ChaosDropSink(int dropEvery = 5) => _dropEvery = dropEvery;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _count) % _dropEvery == 0) return Task.CompletedTask;
        if (result.IsSuccess && result.Value != null) lock (_results) _results.Add(result.Value);
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

internal class ChaosSlowTransformer<T> : ITransformer<T, T>
{
    private readonly int _delayMs;
    public ChaosSlowTransformer(int delayMs = 100) => _delayMs = delayMs;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        await Task.Delay(_delayMs, ct);
        return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}
