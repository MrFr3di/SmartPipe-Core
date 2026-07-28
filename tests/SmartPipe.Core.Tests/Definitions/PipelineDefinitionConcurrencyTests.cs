using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineDefinitionConcurrencyTests
{
    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task FactoryDefinition_32ConcurrentStarts_CreateDistinctComponentsAndRunIdentities()
    {
        var key = new PipelineKey("32-concurrent-starts");
        var factoryRelease = NewGate();
        var allSourcesCreated = NewGate();
        var sourceCalls = 0;
        var sources = new ConcurrentBag<ConcurrencySource>();
        var stages = new ConcurrentBag<ConcurrencyTransformer>();
        var sinks = new ConcurrentBag<ConcurrencySink>();

        var source = PipelineComponent.RuntimeOwned<IPipelineSource<int>>(async (_, ct) =>
        {
            var instance = new ConcurrencySource();
            sources.Add(instance);
            if (Interlocked.Increment(ref sourceCalls) == 32)
                allSourcesCreated.TrySetResult(null);

            await factoryRelease.Task.WaitAsync(ct).ConfigureAwait(false);
            return instance;
        });
        var stage = PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(
            (_, _) =>
            {
                var instance = new ConcurrencyTransformer();
                stages.Add(instance);
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(instance);
            });
        var sink = PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, _) =>
        {
            var instance = new ConcurrencySink();
            sinks.Add(instance);
            return ValueTask.FromResult<IPipelineSink<int>>(instance);
        });

        var definition = PipelineDefinitionBuilder
            .From(key, source)
            .Transform(new PipelineStageKey("stage-1"), stage)
            .To(sink);
        var contexts = Enumerable.Range(0, 32)
            .Select(_ => new PipelineActivationContext(key, Guid.NewGuid()))
            .ToArray();

        var starts = contexts
            .Select(context => definition.StartAsync(context, CancellationToken.None))
            .ToArray();
        await allSourcesCreated.Task;
        factoryRelease.TrySetResult(null);
        var runs = await Task.WhenAll(starts);
        await Task.WhenAll(runs.Select(run => run.Completion));
        await Task.WhenAll(runs.Select(run => run.DisposeAsync().AsTask()));

        sourceCalls.Should().Be(32);
        sources.Should().HaveCount(32);
        stages.Should().HaveCount(32);
        sinks.Should().HaveCount(32);
        sources.Distinct().Should().HaveCount(32);
        stages.Distinct().Should().HaveCount(32);
        sinks.Distinct().Should().HaveCount(32);
        runs.Select(run => run.RunId).Should().OnlyHaveUniqueItems();
        runs.Should().OnlyContain(run => run.PipelineKey == key);
        contexts.Select(context => context.RunId).Should().BeEquivalentTo(
            runs.Select(run => run.RunId));
        sources.Should().OnlyContain(instance => instance.DisposeCount == 1);
        stages.Should().OnlyContain(instance => instance.DisposeCount == 1);
        sinks.Should().OnlyContain(instance => instance.DisposeCount == 1);
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ConcurrentFactoryRuns_CancellingOneRun_DoesNotCancelOthers()
    {
        var key = new PipelineKey("independent-cancellation");
        var sources = new ConcurrentDictionary<Guid, ConcurrencySource>();
        var allSourcesCreated = NewGate();
        var sourceCalls = 0;
        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((context, _) =>
                {
                    var instance = new ConcurrencySource(readGate: NewGate());
                    sources[context.RunId] = instance;
                    if (Interlocked.Increment(ref sourceCalls) == 2)
                        allSourcesCreated.TrySetResult(null);
                    return ValueTask.FromResult<IPipelineSource<int>>(instance);
                }))
            .Build();
        var firstContext = new PipelineActivationContext(key, Guid.NewGuid());
        var secondContext = new PipelineActivationContext(key, Guid.NewGuid());
        var first = definition.StartDeferred(firstContext, CancellationToken.None);
        var second = definition.StartDeferred(secondContext, CancellationToken.None);

        await allSourcesCreated.Task;
        var firstSource = sources[firstContext.RunId];
        var secondSource = sources[secondContext.RunId];
        await Task.WhenAll(firstSource.ReadEntered.Task, secondSource.ReadEntered.Task);

        await first.Run.CancelAsync();
        secondSource.ReadGate!.TrySetResult(null);

        var firstError = await Record.ExceptionAsync(() => first.Completion);
        await second.Completion;

        firstError.Should().BeAssignableTo<OperationCanceledException>();
        second.Run.State.Should().Be(PipelineRunState.Completed);
        firstSource.DisposeCount.Should().Be(1);
        secondSource.DisposeCount.Should().Be(1);
        sourceCalls.Should().Be(2);
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ConcurrentRunDispose_DisposesOnlyTheSelectedRunInstances()
    {
        var key = new PipelineKey("independent-disposal");
        var sources = new ConcurrentDictionary<Guid, ConcurrencySource>();
        var stages = new ConcurrentDictionary<Guid, ConcurrencyTransformer>();
        var sinks = new ConcurrentDictionary<Guid, ConcurrencySink>();
        var allComponentsCreated = NewGate();
        var componentsCreated = 0;

        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((context, _) =>
                {
                    var instance = new ConcurrencySource(readGate: NewGate());
                    sources[context.RunId] = instance;
                    SignalAllComponentsCreated();
                    return ValueTask.FromResult<IPipelineSource<int>>(instance);
                }))
            .Transform(
                new PipelineStageKey("stage-1"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((context, _) =>
                {
                    var instance = new ConcurrencyTransformer();
                    stages[context.RunId] = instance;
                    SignalAllComponentsCreated();
                    return ValueTask.FromResult<IPipelineTransformer<int, int>>(instance);
                }))
            .To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>((context, _) =>
            {
                var instance = new ConcurrencySink();
                sinks[context.RunId] = instance;
                SignalAllComponentsCreated();
                return ValueTask.FromResult<IPipelineSink<int>>(instance);
            }));
        var firstContext = new PipelineActivationContext(key, Guid.NewGuid());
        var secondContext = new PipelineActivationContext(key, Guid.NewGuid());
        var first = definition.StartDeferred(firstContext, CancellationToken.None);
        var second = definition.StartDeferred(secondContext, CancellationToken.None);

        await allComponentsCreated.Task;
        var firstSource = sources[firstContext.RunId];
        var secondSource = sources[secondContext.RunId];
        await Task.WhenAll(firstSource.ReadEntered.Task, secondSource.ReadEntered.Task);

        var firstDispose = first.Run.DisposeAsync().AsTask();
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(10));
        _ = await Record.ExceptionAsync(() => first.Completion);

        firstSource.DisposeCount.Should().Be(1);
        stages[firstContext.RunId].DisposeCount.Should().Be(1);
        sinks[firstContext.RunId].DisposeCount.Should().Be(1);
        secondSource.DisposeCount.Should().Be(0);
        stages[secondContext.RunId].DisposeCount.Should().Be(0);
        sinks[secondContext.RunId].DisposeCount.Should().Be(0);
        second.Completion.IsCompleted.Should().BeFalse();

        secondSource.ReadGate!.TrySetResult(null);
        await second.Completion;
        await second.Run.DisposeAsync();

        secondSource.DisposeCount.Should().Be(1);
        stages[secondContext.RunId].DisposeCount.Should().Be(1);
        sinks[secondContext.RunId].DisposeCount.Should().Be(1);

        void SignalAllComponentsCreated()
        {
            if (Interlocked.Increment(ref componentsCreated) == 6)
                allComponentsCreated.TrySetResult(null);
        }
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task CompileAndStartRace_UsesOneCachedPlan()
    {
        var key = new PipelineKey("compile-start-race");
        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new ConcurrencySource())))
            .Build();
        using var ready = new CountdownEvent(32);
        var release = NewGate();
        var workers = Enumerable.Range(0, 32)
            .Select(async index =>
            {
                ready.Signal();
                await release.Task.ConfigureAwait(false);
                if (index % 2 == 0)
                {
                    _ = definition.GetExecutionPlan();
                }
                else
                {
                    var run = await definition.StartAsync(
                        new PipelineActivationContext(key, Guid.NewGuid()),
                        CancellationToken.None);
                    await run.Completion.ConfigureAwait(false);
                    await run.DisposeAsync().ConfigureAwait(false);
                }

                return definition.GetExecutionPlan();
            })
            .ToArray();

        ready.Wait();
        release.TrySetResult(null);
        var plans = await Task.WhenAll(workers);

        plans.Should().OnlyContain(plan => ReferenceEquals(plan, plans[0]));
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ConcurrentDisposeAndNaturalCompletion_DisposesEachComponentExactlyOnce()
    {
        var key = new PipelineKey("dispose-natural-completion");
        var sourceCreated = NewCompletionSource<ConcurrencySource>();
        var stageCreated = NewCompletionSource<ConcurrencyTransformer>();
        var sinkCreated = NewCompletionSource<ConcurrencySink>();
        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                {
                    var source = new ConcurrencySource(
                        readGate: NewGate(),
                        disposeGate: NewGate());
                    sourceCreated.TrySetResult(source);
                    return ValueTask.FromResult<IPipelineSource<int>>(source);
                }))
            .Transform(
                new PipelineStageKey("stage-1"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(
                    (_, _) =>
                    {
                        var stage = new ConcurrencyTransformer();
                        stageCreated.TrySetResult(stage);
                        return ValueTask.FromResult<IPipelineTransformer<int, int>>(stage);
                    }))
            .To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>(
                (_, _) =>
                {
                    var sink = new ConcurrencySink();
                    sinkCreated.TrySetResult(sink);
                    return ValueTask.FromResult<IPipelineSink<int>>(sink);
                }));
        var operation = definition.StartDeferred(
            new PipelineActivationContext(key, Guid.NewGuid()),
            CancellationToken.None);
        var source = await sourceCreated.Task;
        var stage = await stageCreated.Task;
        var sink = await sinkCreated.Task;
        await source.ReadEntered.Task;

        source.ReadGate!.TrySetResult(null);
        await source.DisposeEntered.Task;
        var dispose = operation.Run.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();

        source.DisposeGate!.TrySetResult(null);
        await dispose;
        _ = await Record.ExceptionAsync(() => operation.Completion);

        source.DisposeCount.Should().Be(1);
        stage.DisposeCount.Should().Be(1);
        sink.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ActivationCancellationAndExternalDispose_CompleteWithoutDeadlock()
    {
        var key = new PipelineKey("cancel-external-dispose");
        var factoryEntered = NewGate();
        var factoryCalls = 0;
        var definition = PipelineDefinitionBuilder
            .From(
                key,
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(async (_, ct) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    factoryEntered.TrySetResult(null);
                    await NewGate().Task.WaitAsync(ct).ConfigureAwait(false);
                    return new ConcurrencySource();
                }))
            .Build();
        var operation = definition.StartDeferred(
            new PipelineActivationContext(key, Guid.NewGuid()),
            CancellationToken.None);

        await factoryEntered.Task;
        var cancel = operation.Run.CancelAsync().AsTask();
        var dispose = operation.Run.DisposeAsync().AsTask();
        var all = Task.WhenAll(cancel, dispose, operation.Completion);
        var error = await Record.ExceptionAsync(
            () => all.WaitAsync(TimeSpan.FromSeconds(10)));

        error.Should().NotBeOfType<TimeoutException>();
        operation.Completion.IsCompleted.Should().BeTrue();
        factoryCalls.Should().Be(1);
    }

    private static TaskCompletionSource<object?> NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ConcurrencySource : IPipelineSource<int>
    {
        private readonly TaskCompletionSource<object?>? _readGate;
        private readonly TaskCompletionSource<object?>? _disposeGate;
        private int _disposeCount;

        public ConcurrencySource(
            TaskCompletionSource<object?>? readGate = null,
            TaskCompletionSource<object?>? disposeGate = null)
        {
            _readGate = readGate;
            _disposeGate = disposeGate;
        }

        public TaskCompletionSource<object?> ReadEntered { get; } = NewGate();

        public TaskCompletionSource<object?> DisposeEntered { get; } = NewGate();

        public TaskCompletionSource<object?>? ReadGate => _readGate;

        public TaskCompletionSource<object?>? DisposeGate => _disposeGate;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadEntered.TrySetResult(null);
            if (_readGate is not null)
                await _readGate.Task.WaitAsync(ct).ConfigureAwait(false);

            yield break;
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposeEntered.TrySetResult(null);
            if (_disposeGate is not null)
                await _disposeGate.Task.ConfigureAwait(false);
        }
    }

    private sealed class ConcurrencyTransformer : IPipelineTransformer<int, int>
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrencySink : IPipelineSink<int>
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
