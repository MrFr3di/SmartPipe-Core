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
        const int uniqueParallelism = 17;
        var stoppedActivities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SmartPipe.Core",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        await using var pipelineRun = PipelineBuilder
            .FromFactory<int>(_ => new ActivityTestSource(traceId, 1))
            .TransformFactory<int>(_ => new ActivityTestTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxDegreeOfParallelism = uniqueParallelism,
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .ToFactory(_ => new ActivityTestSink());

        await pipelineRun.Completion;

        var activities = stoppedActivities.ToArray();
        activities.Should().Contain(activity => activity.Source.Name == "SmartPipe.Core");
        activities.Should().Contain(activity => activity.OperationName == "Pipeline.Run");
        activities.Should().Contain(activity => activity.OperationName == "Transform");

        var run = activities.First(activity =>
            activity.OperationName == "Pipeline.Run"
            && Equals(activity.GetTagItem("smartpipe.parallelism"), uniqueParallelism));
        run.GetTagItem("smartpipe.parallelism").Should().Be(uniqueParallelism);
        run.Status.Should().Be(ActivityStatusCode.Ok);

        var transform = activities.Single(activity =>
            activity.OperationName == "Transform"
            && Equals(activity.GetTagItem("smartpipe.trace_id"), traceId));
        transform.GetTagItem("smartpipe.trace_id").Should().Be(traceId);
    }

    private sealed class ActivityTestSource(ulong traceId, params int[] items) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<int>.Create(item, "activity-test", "run", traceId);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ActivityTestTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ActivityTestSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
