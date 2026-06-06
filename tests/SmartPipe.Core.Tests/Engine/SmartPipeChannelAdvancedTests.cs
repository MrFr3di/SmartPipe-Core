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
    public async Task DrainAsync_DuringRunAsync_ShouldWaitForAcceptedWork()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transformer = new BlockingPassthroughTransformer<int>(release);
        var channel = new SmartPipeChannel<int, int>();
        channel.AddSource(new SimpleSource<int>(1));
        channel.AddTransformer(transformer);
        channel.AddSink(new CollectionSink<int>());

        var runTask = channel.RunAsync();
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = channel.DrainAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        drainTask.IsCompleted.Should().BeFalse(
            "drain must wait for work accepted by a direct RunAsync invocation");

        release.SetResult();
        await drainTask;
        await runTask;
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

    private sealed class BlockingPassthroughTransformer<T> : ITransformer<T, T>
    {
        private readonly TaskCompletionSource _release;

        public BlockingPassthroughTransformer(TaskCompletionSource release)
        {
            _release = release;
        }

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<T>> TransformAsync(
            ProcessingContext<T> ctx,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
