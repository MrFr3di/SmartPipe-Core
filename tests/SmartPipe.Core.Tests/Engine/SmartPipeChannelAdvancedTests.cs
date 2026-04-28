using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelAdvancedTests
{
    [Fact]
    public async Task PauseResume_ShouldNotProcessDuringPause()
    {
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        var transformer = new PassthroughTransformer<int>();
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
        await channel.Invoking(c => c.DrainAsync(TimeSpan.FromSeconds(1))).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_WithOnMetrics_ShouldCallDelegate()
    {
        var metricsList = new List<SmartPipeMetrics>();
        var options = new SmartPipeChannelOptions { OnMetrics = m => { lock (metricsList) metricsList.Add(m); } };
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();
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
        var transformer = new PassthroughTransformer<string>();
        var channel = new SmartPipeChannel<string, string>();
        channel.AddTransformer(transformer);
        var ctx = new ProcessingContext<string>("hello");
        var result = await channel.ProcessSingleAsync(ctx);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
    }

    [Fact]
    public void State_Default_ShouldBeNotStarted()
    {
        new SmartPipeChannel<int, int>().State.Should().Be(PipelineState.NotStarted);
    }

    [Fact]
    public async Task State_AfterRun_ShouldBeCompleted()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(new SimpleSource<int>(1));
        pipe.AddTransformer(new PassthroughTransformer<int>());
        pipe.AddSink(new CollectionSink<int>());
        await pipe.RunAsync();
        pipe.State.Should().Be(PipelineState.Completed);
    }

    [Fact]
    public void Cancel_ShouldChangeState()
    {
        var pipe = new SmartPipeChannel<int, int>();
        pipe.Cancel();
        pipe.State.Should().Be(PipelineState.Cancelled);
    }
}
