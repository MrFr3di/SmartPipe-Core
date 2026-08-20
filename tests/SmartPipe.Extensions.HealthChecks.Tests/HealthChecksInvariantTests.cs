using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks.Tests;

public sealed class HealthChecksInvariantTests
{
    [Fact]
    public void ReadinessRejectsNonFiniteQueueThreshold()
    {
        var failures = SmartPipeReadinessOptionsValidator.Validate(new SmartPipeReadinessOptions
        {
            QueueUtilizationDegradedThreshold = double.NaN,
        });

        Assert.Contains(failures, failure => failure.Contains(nameof(SmartPipeReadinessOptions.QueueUtilizationDegradedThreshold), StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateWithStandardHealthRegistrationFailsOptionsMaterialization()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddCheck(
            "smartpipe:liveness:orders",
            () => new HealthCheckResult(HealthStatus.Healthy));
        services.AddSmartPipe().AddPipeline(Definition("orders")).AddLiveness();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value);
    }

    [Fact]
    public void ActiveCompletedSnapshotIsNotReadinessHealthy()
    {
        var run = Run(PipelineRunState.Completed);
        var observation = Observation([run]);
        var result = new SmartPipeReadinessPolicy().Evaluate(
            observation,
            new(SmartPipeReadinessRunRequirement.ActiveRunRequired, true, false,
                TimeSpan.FromMinutes(1), null, HealthStatus.Unhealthy,
                HealthStatus.Degraded, null, HealthStatus.Degraded, 10),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task InvalidTerminalTimestampIsSanitizedAtHealthBoundary()
    {
        var services = new ServiceCollection();
        services.AddOptions<SmartPipeLivenessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var key = new PipelineKey("orders");
        var source = new FixedSource(new SmartPipePipelineObservation
        {
            PipelineKey = key,
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            ActiveRuns = [],
            LatestTerminal = new SmartPipeTerminalRunObservation
            {
                Identity = new SmartPipeRunIdentity { PipelineKey = key, RunId = Guid.NewGuid() },
                InputType = typeof(int),
                OutputType = typeof(int),
                Outcome = SmartPipeRunObservationOutcome.Completed,
                StartedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
                CompletedAtUtc = DateTimeOffset.UnixEpoch,
                Metrics = SmartPipeMetricsSnapshot.Empty,
                InputCapacity = 10,
                OutputCapacity = 10,
                Sequence = 1,
            },
        });
        var check = new SmartPipePipelineLivenessHealthCheck(
            key,
            source,
            provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(),
            TimeProvider.System);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("probe", check, HealthStatus.Degraded, null),
        };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("CompletedAt", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservationForDifferentRequestedKeyIsSanitized()
    {
        var services = new ServiceCollection();
        services.AddOptions<SmartPipeLivenessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var requested = new PipelineKey("orders");
        var check = new SmartPipePipelineLivenessHealthCheck(
            requested,
            new FixedSource(Observation([]) with { PipelineKey = new PipelineKey("replay") }),
            provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(),
            TimeProvider.System);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("probe", check, HealthStatus.Degraded, null),
        };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    private static SmartPipePipelineObservation Observation(IReadOnlyList<SmartPipeRunSnapshot> active) => new()
    {
        PipelineKey = new PipelineKey("orders"),
        CapturedAtUtc = DateTimeOffset.UnixEpoch,
        ActiveRuns = active,
    };

    private static SmartPipeRunSnapshot Run(PipelineRunState state) => new()
    {
        Identity = new SmartPipeRunIdentity { PipelineKey = new PipelineKey("orders"), RunId = Guid.NewGuid() },
        InputType = typeof(int),
        OutputType = typeof(int),
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        State = state,
        Metrics = SmartPipeMetricsSnapshot.Empty,
        InputCapacity = 10,
        OutputCapacity = 10,
    };

    private sealed class FixedSource(SmartPipePipelineObservation observation) : ISmartPipeRunObservationSource
    {
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey) => observation;
        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => [observation];
    }

    private static PipelineDefinition<int, int> Definition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
            .Build();

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
