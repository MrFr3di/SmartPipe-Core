using System.Collections.Concurrent;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineActivationRollbackTests
{
    [Fact]
    public async Task SourceFactoryFailure_DoesNotInitializeOrDispose()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("source factory");
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            {
                events.Enqueue("source.factory");
                throw primary;
            }));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal("source.factory");
    }

    [Fact]
    public async Task SourceFactoryNull_FailsBeforeInitializationOrCleanup()
    {
        var events = new ConcurrentQueue<string>();
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            {
                events.Enqueue("source.factory");
                return ValueTask.FromResult<IPipelineSource<int>>(null!);
            }));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        events.Should().Equal("source.factory");
    }

    [Fact]
    public async Task SourceInitializeFailure_RollsBackSourceAndPreservesPrimary()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("source initialize");
        var source = new ActivationRecordingSource(events) { InitializeError = primary };
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            {
                events.Enqueue("source.factory");
                return ValueTask.FromResult<IPipelineSource<int>>(source);
            }));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal("source.factory", "source.init", "source.dispose");
    }

    [Fact]
    public async Task FirstStageFactoryFailure_RollsBackSource()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("stage factory");
        var source = new ActivationRecordingSource(events);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(source)))
            .Transform(
                new PipelineStageKey("first"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                {
                    events.Enqueue("stage.factory");
                    throw primary;
                }))
            .Build();

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal("source.init", "stage.factory", "source.dispose");
    }

    [Fact]
    public async Task StageInitializeFailure_RollsBackInReverseOrder()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("stage initialize");
        var source = new ActivationRecordingSource(events);
        var transformer = new ActivationRecordingTransformer(events) { InitializeError = primary };
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            {
                events.Enqueue("source.factory");
                return ValueTask.FromResult<IPipelineSource<int>>(source);
            }),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
            {
                events.Enqueue("stage.factory");
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(transformer);
            }));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal(
            "source.factory", "source.init",
            "stage.factory", "stage.init",
            "stage.dispose", "source.dispose");
    }

    [Fact]
    public async Task LaterStageFactoryFailure_RollsBackEarlierStageAndSource()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("second stage factory");
        var source = new ActivationRecordingSource(events);
        var first = new ActivationRecordingTransformer(events, "stage1");
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(source)))
            .Transform(
                new PipelineStageKey("first"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(first)))
            .Transform(
                new PipelineStageKey("second"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                {
                    events.Enqueue("stage2.factory");
                    throw primary;
                }))
            .Build();

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal(
            "source.init", "stage1.init", "stage2.factory",
            "stage1.dispose", "source.dispose");
    }

    [Fact]
    public async Task LaterStageInitializeFailure_RollsBackAllCreatedStagesAndSource()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("second stage initialize");
        var source = new ActivationRecordingSource(events);
        var first = new ActivationRecordingTransformer(events, "stage1");
        var second = new ActivationRecordingTransformer(events, "stage2")
        {
            InitializeError = primary,
        };
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(source)))
            .Transform(
                new PipelineStageKey("first"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(first)))
            .Transform(
                new PipelineStageKey("second"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(second)))
            .Build();

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal(
            "source.init", "stage1.init", "stage2.init",
            "stage2.dispose", "stage1.dispose", "source.dispose");
    }

    [Fact]
    public async Task RollbackDisposeFailures_AreAggregatedInReverseCleanupOrder()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("stage initialize");
        var sourceDispose = new IOException("source dispose");
        var stageDispose = new TimeoutException("stage dispose");
        var source = new ActivationRecordingSource(events) { DisposeError = sourceDispose };
        var transformer = new ActivationRecordingTransformer(events)
        {
            InitializeError = primary,
            DisposeError = stageDispose,
        };
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(source)),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<int, int>>(transformer)));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<PipelineActivationException>();
        error.Which.InnerException.Should().BeSameAs(primary);
        error.Which.CleanupExceptions.Should().Equal(stageDispose, sourceDispose);
        events.Should().Equal("source.init", "stage.init", "stage.dispose", "source.dispose");
    }

    [Fact]
    public async Task SinkFactoryFailure_RollsBackStagesAndSource()
    {
        var events = new ConcurrentQueue<string>();
        var source = new ActivationRecordingSource(events);
        var transformer = new ActivationRecordingTransformer(events);
        var primary = new InvalidOperationException("sink factory");
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(source)),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<int, int>>(transformer)),
            sink: PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, _) =>
            {
                events.Enqueue("sink.factory");
                throw primary;
            }));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal(
            "source.init", "stage.init", "sink.factory",
            "stage.dispose", "source.dispose");
    }

    [Fact]
    public async Task SinkInitializeFailure_RollsBackSinkStageAndSource()
    {
        var events = new ConcurrentQueue<string>();
        var primary = new InvalidOperationException("sink initialize");
        var sink = new ActivationRecordingSink(events) { InitializeError = primary };
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(
                    new ActivationRecordingSource(events))),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<int, int>>(
                    new ActivationRecordingTransformer(events))),
            sink: PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSink<int>>(sink)));

        var act = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Should().BeSameAs(primary);
        events.Should().Equal(
            "source.init", "stage.init", "sink.init",
            "sink.dispose", "stage.dispose", "source.dispose");
    }

    [Fact]
    public async Task ReadyPublicationFailure_RollsBackFullRuntimeGraphAndSkipsNonRuntimeOwnership()
    {
        var events = new ConcurrentQueue<string>();
        var expected = new InvalidOperationException("started event failed");
        var observer = new ReadinessGateObserver { StartedEventException = expected };
        var scopeStage = new ActivationRecordingTransformer(events, "scope-stage");
        var definition = PipelineDefinitionBuilder
            .From(
                new PipelineKey("ready-rollback"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                        new ActivationRecordingSource(events))))
            .WithObserver(
                observer,
                ObserverReliability.Critical,
                ObserverFailurePolicy.FaultPipeline)
            .Transform(
                new PipelineStageKey("scope-stage"),
                PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>(
                    (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(scopeStage)))
            .Transform(
                new PipelineStageKey("runtime-stage"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(
                    (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(
                        new ActivationRecordingTransformer(events, "runtime-stage"))))
            .To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>(
                (_, _) => ValueTask.FromResult<IPipelineSink<int>>(
                    new ActivationRecordingSink(events))));

        var start = () => definition.StartAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        var error = await start.Should().ThrowAsync<InvalidOperationException>();

        error.Which.Should().BeSameAs(expected);
        events.Should().Equal(
            "source.init", "scope-stage.init", "runtime-stage.init", "sink.init",
            "sink.dispose", "runtime-stage.dispose", "source.dispose");
        events.Should().NotContain("scope-stage.dispose");
    }
}
