using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineActivationCancellationTests
{
    [Fact]
    public async Task PreCancelledFirstStart_DoesNotConsumeSingleUseDefinition()
    {
        var events = new ConcurrentQueue<string>();
        var source = new ActivationRecordingSource(events);
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.Borrowed<IPipelineSource<int>>(source, initialize: true));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var first = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            cancelled.Token).AsTask();
        await first.Should().ThrowAsync<OperationCanceledException>();

        var graph = await definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        graph.Source.Should().BeSameAs(source);
        events.Should().Equal("source.init");
    }

    [Fact]
    public async Task ContextMismatch_DoesNotConsumeDefinition()
    {
        var source = new ActivationRecordingSource(new ConcurrentQueue<string>());
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.Borrowed<IPipelineSource<int>>(source));

        var wrongContext = new PipelineActivationContext(
            new PipelineKey("other"),
            Guid.NewGuid(),
            new ActivationEmptyServices());
        var mismatch = () => definition.ActivateAsync(wrongContext, CancellationToken.None).AsTask();
        await mismatch.Should().ThrowAsync<ArgumentException>();

        var graph = await definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);
        graph.Source.Should().BeSameAs(source);
    }

    [Fact]
    public async Task MissingServices_DoesNotConsumeDefinition()
    {
        var source = new ActivationRecordingSource(new ConcurrentQueue<string>());
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(source)))
            .WithObserver(new ActivationObserver())
            .Build();

        var missing = () => definition.ActivateAsync(
            new PipelineActivationContext(definition.Key, Guid.NewGuid(), services: null),
            CancellationToken.None).AsTask();
        await missing.Should().ThrowAsync<InvalidOperationException>();

        var graph = await definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key, new ActivationEmptyServices()),
            CancellationToken.None);
        graph.Source.Should().BeSameAs(source);
    }

    [Fact]
    public async Task CancellationAfterClaim_ConsumesDefinition()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ActivationRecordingSource(new ConcurrentQueue<string>());
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(async (_, ct) =>
                {
                    entered.SetResult(null);
                    await release.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return source;
                }))
            .WithObserver(new ActivationObserver())
            .Build();
        using var cancellation = new CancellationTokenSource();
        var first = definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            cancellation.Token).AsTask();

        await entered.Task;
        cancellation.Cancel();
        release.SetResult(null);
        Func<Task> firstActivation = () => first;
        await firstActivation.Should().ThrowAsync<OperationCanceledException>();

        var second = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();
        await second.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConcurrentSecondStart_CreatesNoResources()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var source = new ActivationRecordingSource(new ConcurrentQueue<string>());
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(async (_, _) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    entered.SetResult(null);
                    await release.Task.ConfigureAwait(false);
                    return source;
                }))
            .WithObserver(new ActivationObserver())
            .Build();

        var first = definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();
        await entered.Task;
        var second = () => definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None).AsTask();

        await second.Should().ThrowAsync<InvalidOperationException>();
        release.SetResult(null);
        (await first).Source.Should().BeSameAs(source);
        factoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task CancellationBeforeStageFactory_RollsBackSource()
    {
        var events = new ConcurrentQueue<string>();
        var sourceGate = new ActivationGateSource(events);
        var stageFactoryCalls = 0;
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(sourceGate)),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, ct) =>
            {
                Interlocked.Increment(ref stageFactoryCalls);
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(
                    new ActivationRecordingTransformer(events));
            }));
        using var cancellation = new CancellationTokenSource();
        var activation = definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            cancellation.Token).AsTask();

        await sourceGate.Entered.Task;
        cancellation.Cancel();
        sourceGate.Release.SetResult(null);
        Func<Task> activationAttempt = () => activation;
        await activationAttempt.Should().ThrowAsync<OperationCanceledException>();
        stageFactoryCalls.Should().Be(0);
        events.Should().Contain("source.dispose");
    }

    [Fact]
    public async Task CancellationBeforeSinkFactory_RollsBackStageAndSource()
    {
        var events = new ConcurrentQueue<string>();
        var transformerGate = new ActivationGateTransformer(events);
        var sinkFactoryCalls = 0;
        var definition = ActivationTestSupport.CreateDefinition(
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(
                    new ActivationRecordingSource(events))),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<int, int>>(transformerGate)),
            sink: PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, ct) =>
            {
                Interlocked.Increment(ref sinkFactoryCalls);
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IPipelineSink<int>>(
                    new ActivationRecordingSink(events));
            }));
        using var cancellation = new CancellationTokenSource();
        var activation = definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            cancellation.Token).AsTask();

        await transformerGate.Entered.Task;
        cancellation.Cancel();
        transformerGate.Release.SetResult(null);
        Func<Task> activationAttempt = () => activation;
        await activationAttempt.Should().ThrowAsync<OperationCanceledException>();
        sinkFactoryCalls.Should().Be(0);
        events.Should().Contain("source.dispose").And.Contain("stage.dispose");
    }

    [Fact]
    public async Task CancellationWithRollbackFailure_ProducesActivationExceptionWithOceInner()
    {
        var events = new ConcurrentQueue<string>();
        var disposeError = new IOException("rollback");
        var source = new ActivationGateSource(events)
        {
            ThrowOnCancellation = true,
            DisposeError = disposeError,
        };
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(source)))
            .WithObserver(new ActivationObserver())
            .Build();
        using var cancellation = new CancellationTokenSource();
        var activation = definition.ActivateAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            cancellation.Token).AsTask();

        await source.Entered.Task;
        cancellation.Cancel();
        source.Release.SetResult(null);
        Func<Task> activationAttempt = () => activation;
        var error = await activationAttempt.Should().ThrowAsync<PipelineActivationException>();

        error.Which.InnerException.Should().BeOfType<OperationCanceledException>();
        error.Which.CleanupExceptions.Should().ContainSingle().Which.Should().BeSameAs(disposeError);
        events.Should().Equal("source.init", "source.dispose");
    }
}

internal sealed class ActivationObserver : IPipelineObserver
{
    public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}

internal sealed class ActivationGateSource : IPipelineSource<int>
{
    private readonly ConcurrentQueue<string> _events;

    public ActivationGateSource(ConcurrentQueue<string> events) => _events = events;

    public TaskCompletionSource<object?> Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<object?> Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ThrowOnCancellation { get; set; }

    public Exception? DisposeError { get; set; }

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _events.Enqueue("source.init");
        Entered.SetResult(null);
        await Release.Task.ConfigureAwait(false);
        if (ThrowOnCancellation)
            ct.ThrowIfCancellationRequested();
    }

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        _events.Enqueue("source.dispose");
        if (DisposeError is not null)
            throw DisposeError;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ActivationGateTransformer : IPipelineTransformer<int, int>
{
    private readonly ConcurrentQueue<string> _events;

    public ActivationGateTransformer(ConcurrentQueue<string> events) => _events = events;

    public TaskCompletionSource<object?> Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<object?> Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _events.Enqueue("stage.init");
        Entered.SetResult(null);
        await Release.Task.ConfigureAwait(false);
    }

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync()
    {
        _events.Enqueue("stage.dispose");
        return ValueTask.CompletedTask;
    }
}
