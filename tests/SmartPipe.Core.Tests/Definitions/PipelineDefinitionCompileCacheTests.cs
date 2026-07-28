using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineDefinitionCompileCacheTests
{
    [Fact]
    public async Task ConcurrentPlanAccess_ReturnsOneReference()
    {
        var definition = new PipelineDefinition<int, int>(CreateState(), sink: null);
        using var ready = new CountdownEvent(64);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 64)
            .Select(async _ =>
            {
                ready.Signal();
                await release.Task;
                return definition.GetExecutionPlan();
            })
            .ToArray();

        ready.Wait();
        release.SetResult();

        var plans = await Task.WhenAll(callers);

        plans.Should().OnlyContain(plan => ReferenceEquals(plan, plans[0]));
    }

    [Fact]
    public void InvalidPlanFailure_IsCachedByLazy()
    {
        var duplicate = new PipelineStageKey("normalize");
        var state = CreateState(
            new IPipelineStageDescriptor[]
            {
                CreateStage(duplicate),
                CreateStage(duplicate),
            });
        var definition = new PipelineDefinition<int, int>(state, sink: null);

        var first = Capture(() => definition.GetExecutionPlan());
        var second = Capture(() => definition.GetExecutionPlan());

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeTrue();
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static PipelineDefinitionState<int, int> CreateState(
        IPipelineStageDescriptor[]? stages = null) =>
        new(
            new PipelineKey("orders"),
            RuntimeSource<int>(),
            stages ?? [],
            [],
            PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions()),
            LineageMode.Minimal);

    private static IPipelineStageDescriptor CreateStage(PipelineStageKey key) =>
        new PipelineStageDescriptor<int, int>(
            key,
            RuntimeTransformer<int, int>(),
            StageFailureOptionsSnapshot.Create(StageFailureOptions.Default),
            deadLetterOptions: null,
            name: key.Value);

    private static PipelineComponent<IPipelineSource<T>> RuntimeSource<T>() =>
        PipelineComponent.RuntimeOwned<IPipelineSource<T>>((_, _) =>
            ValueTask.FromResult<IPipelineSource<T>>(new TestSource<T>()));

    private static PipelineComponent<IPipelineTransformer<TInput, TOutput>> RuntimeTransformer<TInput, TOutput>() =>
        PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>((_, _) =>
            ValueTask.FromResult<IPipelineTransformer<TInput, TOutput>>(new TestTransformer<TInput, TOutput>()));

    private sealed class TestSource<T> : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestTransformer<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<TOutput>.Success(default!));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
