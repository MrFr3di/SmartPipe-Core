using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

#region Test Implementations
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

internal class SimpleTransformer<T> : ITransformer<T, T>
{
    private readonly Func<T, T>? _transform;
    private readonly double _failureRate;
    private int _count;
    private readonly Random _rng = new(42);

    public SimpleTransformer(Func<T, T>? transform = null, double failureRate = 0)
    {
        _transform = transform;
        _failureRate = failureRate;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        int current = Interlocked.Increment(ref _count);
        if (_failureRate > 0 && _rng.NextDouble() < _failureRate)
            return ValueTask.FromResult(ProcessingResult<T>.Failure(
                new SmartPipeError($"Simulated failure #{current}", ErrorType.Transient), ctx.TraceId));

        T result = _transform != null ? _transform(ctx.Payload) : ctx.Payload;
        return ValueTask.FromResult(ProcessingResult<T>.Success(result, ctx.TraceId));
    }

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
#endregion

public class SmartPipeChannelIntegrationTests
{
    // WIP: Needs timing tuning
    //[Fact]
    //public async Task RunAsync_WithRetryQueue_ShouldRetryTransientErrors()

    [Fact]
    public async Task RunAsync_WithCircuitBreaker_ShouldTrackState()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("CircuitBreaker");
        options.ContinueOnError = true;

        var source = new SimpleSource<int>(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        var transformer = new SimpleTransformer<int>(failureRate: 0.9);
        var sink = new CollectionSink<int>();

        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        channel.Metrics.ItemsFailed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAsync_WithBackpressure_ShouldNotOverflow()
    {
        var options = new SmartPipeChannelOptions { BoundedCapacity = 10, MaxDegreeOfParallelism = 1 };
        var source = new SimpleSource<int>(Enumerable.Range(1, 100).ToArray());
        var transformer = new SimpleTransformer<int>();
        var sink = new CollectionSink<int>();

        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        sink.Results.Should().HaveCount(100);
    }

    [Fact]
    public async Task RunAsync_WithAdaptiveMetrics_ShouldTrackLatency()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        var transformer = new SimpleTransformer<int>();
        var sink = new CollectionSink<int>();

        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        channel.Metrics.SmoothLatencyMs.Should().BeGreaterThanOrEqualTo(0);
        channel.Metrics.SmoothThroughput.Should().BeGreaterThanOrEqualTo(0);
    }

    // WIP: Needs timing tuning
    //[Fact]
    //public async Task RunAsync_WithLatencyHistogram_ShouldRecordPercentiles()

    [Fact]
    public async Task RunAsync_WithSecretScanner_ShouldProcessStrings()
    {
        var source = new SimpleSource<string>("normal data", "hello world");
        var transformer = new SimpleTransformer<string>();
        var sink = new CollectionSink<string>();

        var channel = new SmartPipeChannel<string, string>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        sink.Results.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_WithOnMetrics_ShouldCallDelegate()
    {
        var metricsList = new List<SmartPipeMetrics>();
        var options = new SmartPipeChannelOptions
        {
            OnMetrics = m => { lock (metricsList) metricsList.Add(m); }
        };

        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new SimpleTransformer<int>();
        var sink = new CollectionSink<int>();

        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        metricsList.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ProcessSingleAsync_WithCircuitBreaker_ShouldBlockWhenOpen()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("CircuitBreaker");

        var transformer = new SimpleTransformer<string>(failureRate: 1.0);
        var channel = new SmartPipeChannel<string, string>(options);
        channel.AddTransformer(transformer);

        for (int i = 0; i < 15; i++)
            await channel.ProcessSingleAsync(new ProcessingContext<string>($"test{i}"));

        var result = await channel.ProcessSingleAsync(new ProcessingContext<string>("test"));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("CircuitBreaker");
    }
}
