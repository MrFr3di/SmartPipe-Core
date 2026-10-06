using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks.Tests;

public sealed class HealthChecksRuntimeIntegrationTests
{
    [Fact]
    public async Task ThirtyTwoHealthServiceEvaluationsOverlapActualRunStartAndCompletion()
    {
        const int concurrentRuns = 32;
        var runGate = new RunGate(concurrentRuns);
        var observationGate = new ObservationGate(concurrentRuns);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("runtime-health"),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new GatedSource(runGate))))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var registration = services.AddSmartPipe().AddPipeline(definition);
        var originalSource = services.Single(descriptor =>
            descriptor.ServiceType == typeof(ISmartPipeRunObservationSource));
        services.Remove(originalSource);
        services.AddSingleton<ISmartPipeRunObservationSource>(provider =>
            new BlockingObservationSource(
                originalSource.ImplementationFactory!(provider),
                observationGate));
        registration.AddLiveness();
        registration.AddReadiness(options =>
            options.RunRequirement = SmartPipeReadinessRunRequirement.RegistrationOnly);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var health = provider.GetRequiredService<HealthCheckService>();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runsTask = Task.WhenAll(Enumerable.Range(0, concurrentRuns).Select(async _ =>
        {
            await startGate.Task;
            return await factory.StartAsync(TestContext.Current.CancellationToken);
        }));
        var reportsTask = Task.WhenAll(Enumerable.Range(0, concurrentRuns).Select(async _ =>
        {
            await startGate.Task;
            return await health.CheckHealthAsync(TestContext.Current.CancellationToken);
        }));

        startGate.SetResult();
        var runs = await runsTask;
        await runGate.AllStarted;
        await observationGate.AllCaptured;

        runGate.Release();
        await Task.WhenAll(runs.Select(async run =>
        {
            await run.Completion;
            await run.DisposeAsync();
        }));
        observationGate.Release();

        var reports = await reportsTask;
        Assert.Equal(concurrentRuns, reports.Length);
        Assert.All(reports, report =>
        {
            Assert.Equal(HealthStatus.Healthy, report.Status);
            Assert.Equal(HealthStatus.Healthy, report.Entries["smartpipe:liveness:runtime-health"].Status);
            Assert.Equal(HealthStatus.Healthy, report.Entries["smartpipe:readiness:runtime-health"].Status);
            Assert.All(report.Entries.Values, entry =>
            {
                Assert.Equal("runtime-health", entry.Data["smartpipe.pipeline_key"]);
                Assert.All(entry.Data.Values, value =>
                    Assert.True(value is string or bool or int or long or double));
            });
        });

        var terminal = provider.GetRequiredService<ISmartPipeRunObservationSource>()
            .Capture(definition.Key)
            .LatestTerminal;
        Assert.NotNull(terminal);
        Assert.Equal(concurrentRuns, terminal!.Sequence);
        Assert.Equal(definition.Key, terminal.Identity.PipelineKey);
    }

    private sealed class ObservationGate(int expectedCaptures)
    {
        private readonly TaskCompletionSource _allCaptured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _captured;

        internal Task AllCaptured => _allCaptured.Task;

        internal void Captured()
        {
            if (Interlocked.Increment(ref _captured) == expectedCaptures)
                _allCaptured.TrySetResult();
        }

        internal void Release() => _release.TrySetResult();

        internal void WaitForRelease() => _release.Task.GetAwaiter().GetResult();
    }

    private sealed class BlockingObservationSource(
        object source,
        ObservationGate gate) : ISmartPipeRunObservationSource
    {
        private readonly ISmartPipeRunObservationSource _source =
            (ISmartPipeRunObservationSource)source;

        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey)
        {
            var observation = _source.Capture(pipelineKey);
            gate.Captured();
            gate.WaitForRelease();
            return observation;
        }

        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => _source.CaptureAll();
    }

    private sealed class RunGate(int expectedRuns)
    {
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        internal Task AllStarted => _allStarted.Task;

        internal void Started()
        {
            if (Interlocked.Increment(ref _started) == expectedRuns)
                _allStarted.TrySetResult();
        }

        internal void Release() => _release.TrySetResult();

        internal Task WaitForRelease(CancellationToken cancellationToken) => _release.Task.WaitAsync(cancellationToken);
    }

    private sealed class GatedSource(RunGate gate) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            gate.Started();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await gate.WaitForRelease(ct).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
