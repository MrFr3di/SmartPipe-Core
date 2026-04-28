using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelEdgeCaseTests
{
    [Fact]
    public async Task RunAsync_WithRetryQueueFeature_ShouldNotThrow()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("RetryQueue"); options.EnableFeature("CircuitBreaker");
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source); channel.AddTransformer(transformer); channel.AddSink(sink);
        await channel.Invoking(c => c.RunAsync()).Should().NotThrowAsync();
        sink.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunAsync_WithAllFeaturesEnabled_ShouldNotThrow()
    {
        var options = new SmartPipeChannelOptions();
        foreach (var flag in new[] { "RetryQueue", "CircuitBreaker", "DebugSampling", "CuckooFilter", "JumpHash" })
            options.EnableFeature(flag);
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source); channel.AddTransformer(transformer); channel.AddSink(sink);
        await channel.Invoking(c => c.RunAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_EmptySource_ShouldComplete()
    {
        var source = new SimpleSource<int>();
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(source); channel.AddTransformer(transformer); channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WithFailingTransformer_ContinueOnError_ShouldNotStop()
    {
        var options = new SmartPipeChannelOptions { ContinueOnError = true };
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        var transformer = new SimpleTransformer<int>(failureRate: 0.5);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source); channel.AddTransformer(transformer); channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_WithFailingTransformer_StopOnError_ShouldStopEarly()
    {
        var options = new SmartPipeChannelOptions { ContinueOnError = false };
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        var transformer = new SimpleTransformer<int>(failureRate: 0.9);
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source); channel.AddTransformer(transformer); channel.AddSink(sink);
        await channel.RunAsync();
        sink.Results.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Constructor_NullOptions_ShouldThrow()
    {
        Action act = () => new SmartPipeChannel<int, int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteWithoutError()
    {
        var channel = new SmartPipeChannel<int, int>();
        await channel.Invoking(c => c.DisposeAsync().AsTask()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_WithJumpHashFeature_ShouldNotThrow()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("JumpHash");
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();
        var sink = new CollectionSink<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source); channel.AddTransformer(transformer); channel.AddSink(sink);
        await channel.Invoking(c => c.RunAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public void Metrics_FeatureFlag_ShouldBeEnabledByDefault()
    {
        new SmartPipeChannelOptions().IsEnabled("Metrics").Should().BeTrue();
    }

    [Fact]
    public async Task CreateDashboard_AfterRun_ShouldHaveMetrics()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1, 2, 3));
        pipe.AddTransformer(new PassthroughTransformer<int>());
        pipe.AddSink(new CollectionSink<int>());
        await pipe.RunAsync();
        var dashboard = pipe.CreateDashboard();
        dashboard.State.Should().Be(PipelineState.Completed);
        dashboard.Current.Should().BeGreaterThanOrEqualTo(0);
        dashboard.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        dashboard.Metrics.Should().ContainKey("items_processed");
    }
}
