using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class ObservationLifecycleTests
{
    [Fact]
    public async Task PreCancellationProducesNoTerminalObservation()
    {
        var (provider, definition) = BuildProvider("pre-cancel", static (_, _) =>
            ValueTask.FromResult<IPipelineSource<int>>(new EmptySource()));
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.StartAsync(cancellation.Token));

        Assert.Null(source.Capture(definition.Key).LatestTerminal);
    }

    [Fact]
    public async Task ActivationFailureIsPublishedAfterCleanupWithoutExceptionGraph()
    {
        var failure = new InvalidOperationException("sensitive activation message");
        var (provider, definition) = BuildProvider("activation", (_, _) => throw failure);
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));
        var terminal = Assert.IsType<SmartPipeTerminalRunObservation>(
            source.Capture(definition.Key).LatestTerminal);

        Assert.Same(failure, observed);
        Assert.Equal(SmartPipeRunObservationOutcome.ActivationFailed, terminal.Outcome);
        Assert.NotEqual(Guid.Empty, terminal.Identity.RunId);
        Assert.Equal(SmartPipeMetricsSnapshot.Empty, terminal.Metrics);
        Assert.Equal(definition.RuntimeOptions.InputCapacity, terminal.InputCapacity);
        Assert.Equal(1024, terminal.OutputCapacity);
        Assert.DoesNotContain(
            typeof(Exception),
            typeof(SmartPipeTerminalRunCandidate).GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public async Task ActivationFailureUsesConfiguredOutputCapacity()
    {
        var (provider, definition) = BuildProvider(
            "activation-capacity",
            static (_, _) => throw new InvalidOperationException("activation"),
            new PipelineRuntimeOptions { OutputCapacity = 37 });
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            37,
            Assert.IsType<SmartPipeTerminalRunObservation>(source.Capture(definition.Key).LatestTerminal)
                .OutputCapacity);
    }

    [Fact]
    public async Task ForeignTokenCancellationDuringActivationPublishesActivationFailure()
    {
        using var foreignCancellation = new CancellationTokenSource();
        var (provider, definition) = BuildProvider(
            "foreign-cancel",
            (_, _) =>
            {
                foreignCancellation.Cancel();
                throw new OperationCanceledException(foreignCancellation.Token);
            });
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            SmartPipeRunObservationOutcome.ActivationFailed,
            source.Capture(definition.Key).LatestTerminal?.Outcome);
    }

    [Fact]
    public async Task CallerTokenCancellationAfterActivationBeginsProducesNoTerminalObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var (provider, definition) = BuildProvider(
            "cancel-during-activation",
            (_, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.StartAsync(cancellation.Token));

        Assert.Null(source.Capture(definition.Key).LatestTerminal);
    }

    [Fact]
    public async Task SuccessfulRunIsActiveBeforeReturnAndTerminalAfterCleanup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (provider, definition) = BuildProvider("success", (_, _) =>
            ValueTask.FromResult<IPipelineSource<int>>(new GateSource(gate)));
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();

        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        var active = source.Capture(definition.Key);

        Assert.Equal(run.RunId, Assert.Single(active.ActiveRuns).Identity.RunId);
        Assert.Null(active.LatestTerminal);

        gate.SetResult();
        await run.Completion;
        var completed = source.Capture(definition.Key);

        Assert.Empty(completed.ActiveRuns);
        Assert.Equal(SmartPipeRunObservationOutcome.Completed, completed.LatestTerminal?.Outcome);
        Assert.Equal(run.InputCapacity, completed.LatestTerminal?.InputCapacity);
        Assert.Equal(run.OutputCapacity, completed.LatestTerminal?.OutputCapacity);
    }

    [Fact]
    public async Task FaultedRunPublishesFaultedTerminal()
    {
        var (provider, definition) = BuildProvider("faulted", static (_, _) =>
            ValueTask.FromResult<IPipelineSource<int>>(new FaultingSource()));
        await using var root = provider;
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var source = root.GetRequiredService<ISmartPipeRunObservationSource>();

        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Completion);

        Assert.Equal(
            SmartPipeRunObservationOutcome.Faulted,
            source.Capture(definition.Key).LatestTerminal?.Outcome);
    }

    private static (ServiceProvider Provider, PipelineDefinition<int, int> Definition) BuildProvider(
        string key,
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineSource<int>>> factory,
        PipelineRuntimeOptions? runtimeOptions = null)
    {
        var builder = PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned(factory));
        if (runtimeOptions is not null)
        {
            builder = builder.WithRuntimeOptions(runtimeOptions);
        }

        var definition = builder.Build();
        var services = new ServiceCollection();
        services.AddSmartPipe().AddPipeline(definition);
        return (services.BuildServiceProvider(), definition);
    }

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GateSource : IPipelineSource<int>
    {
        private readonly TaskCompletionSource _gate;

        internal GateSource(TaskCompletionSource gate) => _gate = gate;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await _gate.Task.WaitAsync(ct);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultingSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("runtime failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
