using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class RunRegistryTests
{
    [Fact]
    public async Task Registry_TracksReadyRunWithEffectiveMetadataAndRemovesAfterCompletion()
    {
        var services = new ServiceCollection();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeDisposal = new ScopeDisposalRecorder();
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2035, 4, 5, 11, 7, 8, TimeSpan.FromHours(5)));
        services.AddSingleton(gate);
        services.AddSingleton(scopeDisposal);
        services.AddScoped<RunScopeMarker>();
        services.AddSingleton<TimeProvider>(timeProvider);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("active"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (context, _) =>
                    {
                        var services = context.Services
                            ?? throw new InvalidOperationException("A scoped provider is required.");
                        services.GetRequiredService<RunScopeMarker>();
                        return ValueTask.FromResult<IPipelineSource<int>>(
                            new GateSource(services.GetRequiredService<TaskCompletionSource>()));
                    }))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                InputCapacity = 17,
                OutputCapacity = 23,
            })
            .Build();
        services.AddSmartPipe().AddPipeline(definition);
        await using var root = services.BuildServiceProvider();
        var registry = root.GetRequiredService<ISmartPipeRunRegistry>();
        var observations = root.GetRequiredService<ISmartPipeRunObservationSource>();
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("active");

        Assert.Same(root.GetRequiredService<SmartPipeRunRegistry>(), registry);
        Assert.Empty(registry.GetActiveRuns(definition.Key));
        Assert.Throws<ArgumentException>(() => registry.GetActiveRuns(default));

        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        var snapshot = Assert.Single(registry.GetActiveRuns(definition.Key));

        Assert.Equal(definition.Key, snapshot.Identity.PipelineKey);
        Assert.Equal(run.RunId, snapshot.Identity.RunId);
        Assert.Equal(typeof(int), snapshot.InputType);
        Assert.Equal(typeof(int), snapshot.OutputType);
        Assert.Equal(new DateTimeOffset(2035, 4, 5, 6, 7, 8, TimeSpan.Zero), snapshot.StartedAtUtc);
        Assert.Equal(PipelineRunState.Running, snapshot.State);
        Assert.Equal(run.Metrics, snapshot.Metrics);
        Assert.Equal(17, snapshot.InputCapacity);
        Assert.Equal(23, snapshot.OutputCapacity);
        Assert.Equal(0, scopeDisposal.DisposeCalls);

        gate.SetResult();
        await run.Completion;

        Assert.Empty(registry.GetActiveRuns(definition.Key));
        Assert.Equal(1, scopeDisposal.DisposeCalls);
        Assert.Equal(
            SmartPipeRunObservationOutcome.Completed,
            observations.Capture(definition.Key).LatestTerminal?.Outcome);
    }

    [Fact]
    public async Task Registry_ReturnsDefensiveSnapshotsOrderedByStartedTimeThenRunId()
    {
        var services = new ServiceCollection();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.AddSingleton(gate);
        var timeProvider = new MutableTimeProvider();
        services.AddSingleton<TimeProvider>(timeProvider);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("ordered"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (context, _) => ValueTask.FromResult<IPipelineSource<int>>(
                        new GateSource(context.Services!.GetRequiredService<TaskCompletionSource>()))))
            .Build();
        services.AddSmartPipe().AddPipeline(definition);
        await using var root = services.BuildServiceProvider();
        var registry = root.GetRequiredService<ISmartPipeRunRegistry>();
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("ordered");
        timeProvider.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(3);
        var latest = await factory.StartAsync(TestContext.Current.CancellationToken);
        timeProvider.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var earliest = await factory.StartAsync(TestContext.Current.CancellationToken);
        timeProvider.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(2);
        var middleFirst = await factory.StartAsync(TestContext.Current.CancellationToken);
        var middleSecond = await factory.StartAsync(TestContext.Current.CancellationToken);
        var runs = new[] { latest, earliest, middleFirst, middleSecond };

        var snapshots = registry.GetActiveRuns(definition.Key);

        Assert.Equal(
            new[] { earliest.RunId }
                .Concat(new[] { middleFirst.RunId, middleSecond.RunId }.Order())
                .Append(latest.RunId),
            snapshots.Select(snapshot => snapshot.Identity.RunId).ToArray());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SmartPipeRunSnapshot>)snapshots).Add(snapshots[0]));

        timeProvider.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(4);
        gate.SetResult();
        await Task.WhenAll(runs.Select(run => run.Completion));
        Assert.Empty(registry.GetActiveRuns(definition.Key));
    }

    [Fact]
    public async Task ExplicitConcurrentDispose_UsesOneCleanupAndRemovesActiveRun()
    {
        var services = new ServiceCollection();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.AddSingleton(gate);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("dispose"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (context, _) => ValueTask.FromResult<IPipelineSource<int>>(
                        new GateSource(context.Services!.GetRequiredService<TaskCompletionSource>()))))
            .Build();
        services.AddSmartPipe().AddPipeline(definition);
        await using var root = services.BuildServiceProvider();
        var registry = root.GetRequiredService<ISmartPipeRunRegistry>();
        var observations = root.GetRequiredService<ISmartPipeRunObservationSource>();
        var factory = root.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("dispose");
        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        Assert.Single(registry.GetActiveRuns(definition.Key));

        var first = run.DisposeAsync().AsTask();
        var second = run.DisposeAsync().AsTask();
        var third = run.DisposeAsync().AsTask();
        await Task.WhenAll(first, second, third);

        Assert.Same(first, second);
        Assert.Same(first, third);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);
        Assert.True(run.Completion.IsCanceled);
        Assert.Empty(registry.GetActiveRuns(definition.Key));
        var terminal = observations.Capture(definition.Key).LatestTerminal;
        Assert.Equal(SmartPipeRunObservationOutcome.Cancelled, terminal?.Outcome);
        Assert.Equal(1, terminal?.Sequence);
    }

    [Fact]
    public void MutableRegistry_RejectsCompatibilityRunWithoutEffectiveCapacities()
    {
        var outputs = Channel.CreateUnbounded<PipelineOutput<int>>();
        var run = new PipelineRun<int>(
            outputs.Reader,
            Task.CompletedTask,
            static () => PipelineRunState.Completed);
        var registry = new SmartPipeRunRegistry();

        Assert.Throws<ArgumentException>(() =>
            registry.Register<int, int>(run, DateTimeOffset.UnixEpoch));
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        internal FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ScopeDisposalRecorder
    {
        internal int DisposeCalls { get; set; }
    }

    private sealed class RunScopeMarker : IDisposable
    {
        private readonly ScopeDisposalRecorder _recorder;

        public RunScopeMarker(ScopeDisposalRecorder recorder) => _recorder = recorder;

        public void Dispose() => _recorder.DisposeCalls++;
    }
}
