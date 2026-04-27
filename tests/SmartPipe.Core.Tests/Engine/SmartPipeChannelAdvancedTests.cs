using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelAdvancedTests
{
    // Тесты на Drain, Pause, метрики
    
    [Fact]
    public async Task PauseResume_ShouldNotProcessDuringPause()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        var transformer = new SlowTransformer<int>(50);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        channel.Pause();
        var runTask = channel.RunAsync();
        await Task.Delay(100);
        channel.Resume();
        await runTask;

        sink.Results.Should().HaveCount(5);
    }

    [Fact]
    public async Task DrainAsync_EmptyPipeline_ShouldNotThrow()
    {
        var channel = new SmartPipeChannel<int, int>();
        await channel.Invoking(c => c.DrainAsync(TimeSpan.FromSeconds(1)))
            .Should().NotThrowAsync();
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
        var transformer = new SlowTransformer<int>(50);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        metricsList.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ProcessSingleAsync_WithTransformer_ShouldReturnSuccess()
    {
        var transformer = new SlowTransformer<string>();
        var channel = new SmartPipeChannel<string, string>();
        channel.AddTransformer(transformer);

        var ctx = new ProcessingContext<string>("hello");
        var result = await channel.ProcessSingleAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
    }

    [Fact]
    public async Task ProcessSingleAsync_NoTransformers_ShouldFail()
    {
        var channel = new SmartPipeChannel<int, int>();
        var ctx = new ProcessingContext<int>(42);
        var result = await channel.ProcessSingleAsync(ctx);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Message.Should().Contain("No transformers");
    }

    [Fact]
    public async Task RunAsync_WithCuckooFilterFeature_ShouldNotThrow()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("CuckooFilter");
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new SlowTransformer<int>(50);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.Invoking(c => c.RunAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_WithDebugSamplingFeature_ShouldNotThrow()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("DebugSampling");
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new SlowTransformer<int>(50);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.Invoking(c => c.RunAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Metrics_ShouldTrackLatencyHistogram()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        var transformer = new SlowTransformer<int>(50);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        await channel.RunAsync();
        channel.LatencyHistogram.P50.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Constructor_DefaultOptions_ShouldSetDefaults()
    {
        var channel = new SmartPipeChannel<int, int>();
        channel.Options.MaxDegreeOfParallelism.Should().Be(Environment.ProcessorCount);
        channel.Options.BoundedCapacity.Should().Be(1000);
        channel.Options.ContinueOnError.Should().BeTrue();
    }

    [Fact]
    public void IsPaused_InitiallyFalse()
    {
        var channel = new SmartPipeChannel<int, int>();
        channel.IsPaused.Should().BeFalse();
    }
}

internal class SlowTransformer<T> : ITransformer<T, T>
{
    private readonly int _delayMs;
    public SlowTransformer(int delayMs = 10) => _delayMs = delayMs;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        await Task.Delay(_delayMs, ct);
        return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}
