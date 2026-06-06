#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Diagnostics;

public class SmartPipeActivityTests
{
    [Fact]
    public async Task RunAsync_ShouldEmitStableActivitySourceAndProcessingActivities()
    {
        const ulong traceId = ulong.MaxValue - 42;
        var stoppedActivities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SmartPipe.Core",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var channel = new SmartPipeChannel<int, int>(
            new SmartPipeChannelOptions { MaxDegreeOfParallelism = 1 });
        channel.AddSource(new ActivityTestSource(traceId, 1));
        channel.AddTransformer(new ActivityTestTransformer());
        channel.AddSink(new ActivityTestSink());

        await channel.RunAsync();

        var activities = stoppedActivities.ToArray();
        activities.Should().Contain(activity => activity.Source.Name == "SmartPipe.Core");
        activities.Should().Contain(activity => activity.OperationName == "Pipeline.Run");
        activities.Should().Contain(activity => activity.OperationName == "Transform");

        var run = activities.First(activity =>
            activity.OperationName == "Pipeline.Run"
            && Equals(activity.GetTagItem("smartpipe.parallelism"), 1));
        run.GetTagItem("smartpipe.parallelism").Should().Be(1);
        run.Status.Should().Be(ActivityStatusCode.Ok);

        var transform = activities.Single(activity =>
            activity.OperationName == "Transform"
            && Equals(activity.GetTagItem("smartpipe.trace_id"), traceId));
        transform.GetTagItem("smartpipe.trace_id").Should().Be(traceId);
    }

    private sealed class ActivityTestSource(ulong traceId, params int[] items) : ISource<int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<int>> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ProcessingContext<int>(item) { TraceId = traceId };
                await Task.Yield();
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class ActivityTestTransformer : ITransformer<int, int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<ProcessingResult<int>> TransformAsync(
            ProcessingContext<int> ctx,
            CancellationToken ct = default) =>
            ValueTask.FromResult(ProcessingResult<int>.Success(ctx.Payload, ctx.TraceId));

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class ActivityTestSink : ISink<int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ProcessingResult<int> result, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
