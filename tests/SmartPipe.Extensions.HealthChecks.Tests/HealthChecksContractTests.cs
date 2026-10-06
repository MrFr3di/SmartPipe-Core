using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks.Tests;

public sealed class HealthChecksContractTests
{
    [Theory]
    [InlineData("orders", "smartpipe:liveness:orders", "smartpipe:readiness:orders", "smartpipe-pipeline:orders")]
    [InlineData("Orders", "smartpipe:liveness:Orders", "smartpipe:readiness:Orders", "smartpipe-pipeline:Orders")]
    [InlineData("a:b", "smartpipe:liveness:a:b", "smartpipe:readiness:a:b", "smartpipe-pipeline:a:b")]
    [InlineData(" A ", "smartpipe:liveness: A ", "smartpipe:readiness: A ", "smartpipe-pipeline: A ")]
    public void NamesPreserveExactKey(string value, string liveness, string readiness, string tag)
    {
        var key = new PipelineKey(value);
        Assert.Equal(liveness, SmartPipeHealthCheckNames.Liveness(key));
        Assert.Equal(readiness, SmartPipeHealthCheckNames.Readiness(key));
        Assert.Equal(tag, SmartPipeHealthCheckNames.PipelineTag(key));
    }

    [Fact]
    public void RegistrationUsesStableTagsAndIsAtomicOnDuplicate()
    {
        var services = new ServiceCollection();
        AddTestLogging(services);
        var registration = services.AddSmartPipe().AddPipeline(Definition("orders"));
        var returned = registration.AddLiveness(
            failureStatus: HealthStatus.Degraded,
            tags: ["z", "custom", "custom", "a"],
            timeout: TimeSpan.FromSeconds(7));

        Assert.Same(registration, returned);
        Assert.Throws<InvalidOperationException>(() => registration.AddLiveness());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var descriptor = Assert.Single(options.Registrations);
        Assert.Equal("smartpipe:liveness:orders", descriptor.Name);
        Assert.Equal(HealthStatus.Degraded, descriptor.FailureStatus);
        Assert.Equal(TimeSpan.FromSeconds(7), descriptor.Timeout);
        Assert.Equal(
            ["smartpipe", "smartpipe-liveness", "smartpipe-pipeline:orders", "a", "custom", "z"],
            descriptor.Tags);
    }

    [Fact]
    public void RegistrationRejectsInvalidNameTagAndTimeout()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(Definition("orders"));

        Assert.Throws<ArgumentException>(() => registration.AddReadiness(name: " "));
        Assert.Throws<ArgumentException>(() => registration.AddReadiness(tags: ["ok", " "]));
        Assert.Throws<ArgumentOutOfRangeException>(() => registration.AddReadiness(timeout: TimeSpan.Zero));
    }

    [Fact]
    public void NamedOptionsAreIsolatedAndInvalidValuesFailFirstMaterialization()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        var orders = builder.AddPipeline(Definition("orders"));
        var replay = builder.AddPipeline(Definition("replay"));
        orders.AddLiveness(options => options.FailOnLatestFault = false);
        replay.AddLiveness(options => options.FailOnActivationFailure = true);
        replay.AddReadiness(options => options.MaximumReportedProblemRuns = 0);
        using var provider = services.BuildServiceProvider();
        var liveness = provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>();
        var readiness = provider.GetRequiredService<IOptionsMonitor<SmartPipeReadinessOptions>>();

        Assert.False(liveness.Get(SmartPipeHealthCheckNames.Liveness(orders.Key)).FailOnLatestFault);
        Assert.False(liveness.Get(SmartPipeHealthCheckNames.Liveness(orders.Key)).FailOnActivationFailure);
        Assert.True(liveness.Get(SmartPipeHealthCheckNames.Liveness(replay.Key)).FailOnLatestFault);
        Assert.True(liveness.Get(SmartPipeHealthCheckNames.Liveness(replay.Key)).FailOnActivationFailure);
        Assert.Throws<OptionsValidationException>(() =>
            readiness.Get(SmartPipeHealthCheckNames.Readiness(replay.Key)));
    }

    [Fact]
    public async Task InvalidNamedOptionsFailGenericHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var registration = builder.Services.AddSmartPipe().AddPipeline(Definition("orders"));
        registration.AddReadiness(options => options.QueuePressureStatus = HealthStatus.Healthy);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AggregateOptionsRejectDuplicateAndUnknownExactKeys()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        builder.AddPipeline(Definition("orders"));
        services.AddHealthChecks().AddSmartPipeAggregateLiveness(options =>
        {
            options.IncludeAllRegisteredPipelines = false;
            options.IncludedPipelines.Add(new PipelineKey("orders"));
            options.IncludedPipelines.Add(new PipelineKey("orders"));
            options.IncludedPipelines.Add(new PipelineKey("Orders"));
        });
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptionsMonitor<SmartPipeAggregateLivenessOptions>>()
                .Get(SmartPipeHealthCheckNames.AggregateLiveness));

        Assert.Contains(error.Failures, failure => failure.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(error.Failures, failure => failure.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HealthCheckServiceDistinguishesLivenessAndReadiness()
    {
        var services = new ServiceCollection();
        AddTestLogging(services);
        var registration = services.AddSmartPipe().AddPipeline(Definition("orders"));
        registration.AddLiveness();
        registration.AddReadiness();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Entries["smartpipe:liveness:orders"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["smartpipe:readiness:orders"].Status);
    }

    [Theory]
    [InlineData(SmartPipeRunObservationOutcome.Completed, true, true)]
    [InlineData(SmartPipeRunObservationOutcome.Cancelled, true, true)]
    [InlineData(SmartPipeRunObservationOutcome.Aborted, true, true)]
    [InlineData(SmartPipeRunObservationOutcome.Faulted, false, true)]
    [InlineData(SmartPipeRunObservationOutcome.ActivationFailed, true, true)]
    public void LivenessOutcomeMatrix(
        SmartPipeRunObservationOutcome outcome,
        bool defaultHealthy,
        bool healthyWhenActive)
    {
        var policy = new SmartPipeLivenessPolicy();
        var terminalOnly = Observation("orders", terminal: Terminal("orders", outcome));
        var active = Observation("orders", [Run("orders", PipelineRunState.Running)], terminalOnly.LatestTerminal);

        var result = policy.Evaluate(
            terminalOnly,
            new(true, false, 10),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);
        var recovered = policy.Evaluate(
            active,
            new(true, true, 10),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.Equal(defaultHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, result.Status);
        Assert.Equal(healthyWhenActive ? HealthStatus.Healthy : HealthStatus.Unhealthy, recovered.Status);
    }

    [Theory]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, null, true)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveRunRequired, null, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Completed, true)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Cancelled, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Aborted, false)]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, SmartPipeRunObservationOutcome.Faulted, false)]
    public void ReadinessAbsentRunMatrix(
        SmartPipeReadinessRunRequirement requirement,
        SmartPipeRunObservationOutcome? outcome,
        bool healthy)
    {
        var observation = Observation("orders", terminal: outcome is null ? null : Terminal("orders", outcome.Value));
        var result = new SmartPipeReadinessPolicy().Evaluate(
            observation,
            Readiness(requirement),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.Equal(healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void ReadinessHonorsActivityAndQueueBoundariesAndWorstSibling()
    {
        var now = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var atGrace = Run("orders", PipelineRunState.Running, started: now.AddMinutes(-1));
        var stale = Run(
            "orders",
            PipelineRunState.Running,
            started: now.AddMinutes(-5),
            metrics: Metrics(lastActivity: now.AddMinutes(-2)));
        var pressured = Run(
            "orders",
            PipelineRunState.Running,
            started: now.AddMinutes(-5),
            metrics: Metrics(inputDepth: 5));
        var options = Readiness(
            requireActivity: true,
            grace: TimeSpan.FromMinutes(1),
            staleAfter: TimeSpan.FromMinutes(1),
            queueThreshold: 0.5);
        var policy = new SmartPipeReadinessPolicy();

        Assert.Equal(
            HealthStatus.Healthy,
            policy.Evaluate(Observation("orders", [atGrace]), options, now, HealthStatus.Unhealthy).Status);
        var result = policy.Evaluate(
            Observation("orders", [
                atGrace with { StartedAtUtc = atGrace.StartedAtUtc.AddTicks(-1) },
                stale,
                pressured]),
            options,
            now,
            HealthStatus.Unhealthy);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(3, result.Data["smartpipe.problem_run_count"]);
        Assert.Equal(0.5, result.Data["smartpipe.max_input_utilization"]);
    }

    [Fact]
    public void ReadinessStaleBoundaryAndClockReversalDoNotFail()
    {
        var started = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var activity = started.AddMinutes(1);
        var run = Run(
            "orders",
            PipelineRunState.Running,
            started,
            Metrics(lastActivity: activity));
        var options = Readiness(staleAfter: TimeSpan.FromMinutes(1));
        var policy = new SmartPipeReadinessPolicy();

        Assert.Equal(
            HealthStatus.Healthy,
            policy.Evaluate(Observation("orders", [run]), options, activity.AddMinutes(1), HealthStatus.Unhealthy).Status);
        Assert.Equal(
            HealthStatus.Healthy,
            policy.Evaluate(Observation("orders", [run]), options, started.AddMinutes(-1), HealthStatus.Unhealthy).Status);
        Assert.Equal(
            HealthStatus.Degraded,
            policy.Evaluate(Observation("orders", [run]), options, activity.AddMinutes(1).AddTicks(1), HealthStatus.Unhealthy).Status);
    }

    [Fact]
    public void InvalidCapacityAndDepthAreRejectedBeforeDivision()
    {
        var invalidCapacity = Run("orders", PipelineRunState.Running) with { InputCapacity = 0 };
        var invalidDepth = Run(
            "orders",
            PipelineRunState.Running,
            metrics: Metrics(inputDepth: -1));
        var policy = new SmartPipeReadinessPolicy();

        Assert.Throws<InvalidOperationException>(() =>
            policy.Evaluate(Observation("orders", [invalidCapacity]), Readiness(queueThreshold: 0.5), DateTimeOffset.UnixEpoch, HealthStatus.Unhealthy));
        Assert.Throws<InvalidOperationException>(() =>
            policy.Evaluate(Observation("orders", [invalidDepth]), Readiness(queueThreshold: 0.5), DateTimeOffset.UnixEpoch, HealthStatus.Unhealthy));
    }

    [Fact]
    public async Task CaptureFailureIsSanitizedAndCallerCancellationEscapes()
    {
        var services = new ServiceCollection();
        services.AddOptions<SmartPipeLivenessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var check = new SmartPipePipelineLivenessHealthCheck(
            new PipelineKey("orders"),
            new ThrowingSource(new InvalidOperationException("sensitive message")),
            provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(),
            TimeProvider.System);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("probe", check, HealthStatus.Degraded, null),
        };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("sensitive", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Data.Values, value => value.ToString()!.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await check.CheckHealthAsync(context, cancellation.Token));

        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var foreignCheck = new SmartPipePipelineLivenessHealthCheck(
            new PipelineKey("orders"),
            new ThrowingSource(new OperationCanceledException(foreign.Token)),
            provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(),
            TimeProvider.System);
        var foreignResult = await foreignCheck.CheckHealthAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, foreignResult.Status);
    }

    [Fact]
    public async Task AggregateChecksUseRegistrationOrderAndBoundProblemKeys()
    {
        var services = new ServiceCollection();
        AddTestLogging(services);
        var builder = services.AddSmartPipe();
        builder.AddPipeline(Definition("a,b"));
        builder.AddPipeline(Definition("orders"));
        services.AddHealthChecks().AddSmartPipeAggregateReadiness(
            options => options.MaximumReportedProblemKeys = 1);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);
        var entry = report.Entries[SmartPipeHealthCheckNames.AggregateReadiness];

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal(2, entry.Data["smartpipe.pipeline_count"]);
        Assert.Equal("a,b", entry.Data["smartpipe.problem_key_0"]);
        Assert.Equal(1, entry.Data["smartpipe.problem_keys_reported"]);
        Assert.Equal(true, entry.Data["smartpipe.problem_keys_truncated"]);
        Assert.DoesNotContain("smartpipe.problem_keys", entry.Data.Keys);
        Assert.All(entry.Data.Values, value =>
            Assert.True(value is string or bool or int or long or double));
    }

    [Fact]
    public async Task ConcurrentHealthChecksRemainDeterministic()
    {
        var services = new ServiceCollection();
        AddTestLogging(services);
        var registration = services.AddSmartPipe().AddPipeline(Definition("orders"));
        registration.AddLiveness();
        registration.AddReadiness();
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        var reports = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            health.CheckHealthAsync(TestContext.Current.CancellationToken)));

        Assert.All(reports, report =>
        {
            Assert.Equal(HealthStatus.Healthy, report.Entries["smartpipe:liveness:orders"].Status);
            Assert.Equal(HealthStatus.Unhealthy, report.Entries["smartpipe:readiness:orders"].Status);
        });
    }

    private static PipelineDefinition<int, int> Definition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
            .Build();

    private static void AddTestLogging(IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    }

    private static SmartPipePipelineObservation Observation(
        string key,
        IReadOnlyList<SmartPipeRunSnapshot>? active = null,
        SmartPipeTerminalRunObservation? terminal = null) => new()
        {
            PipelineKey = new PipelineKey(key),
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            ActiveRuns = active ?? [],
            LatestTerminal = terminal,
        };

    private static SmartPipeRunSnapshot Run(
        string key,
        PipelineRunState state,
        DateTimeOffset? started = null,
        SmartPipeMetricsSnapshot? metrics = null) => new()
        {
            Identity = new SmartPipeRunIdentity { PipelineKey = new PipelineKey(key), RunId = Guid.NewGuid() },
            InputType = typeof(int),
            OutputType = typeof(int),
            StartedAtUtc = started ?? DateTimeOffset.UnixEpoch,
            State = state,
            Metrics = metrics ?? SmartPipeMetricsSnapshot.Empty,
            InputCapacity = 10,
            OutputCapacity = 10,
        };

    private static SmartPipeTerminalRunObservation Terminal(
        string key,
        SmartPipeRunObservationOutcome outcome) => new()
        {
            Identity = new SmartPipeRunIdentity { PipelineKey = new PipelineKey(key), RunId = Guid.NewGuid() },
            InputType = typeof(int),
            OutputType = typeof(int),
            Outcome = outcome,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            CompletedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Metrics = SmartPipeMetricsSnapshot.Empty,
            InputCapacity = 10,
            OutputCapacity = 10,
            Sequence = 1,
        };

    private static SmartPipeReadinessOptionsSnapshot Readiness(
        SmartPipeReadinessRunRequirement requirement = SmartPipeReadinessRunRequirement.ActiveRunRequired,
        bool requireActivity = false,
        TimeSpan? grace = null,
        TimeSpan? staleAfter = null,
        double? queueThreshold = null) => new(
            requirement,
            true,
            requireActivity,
            grace ?? TimeSpan.FromMinutes(1),
            staleAfter,
            HealthStatus.Unhealthy,
            HealthStatus.Degraded,
            queueThreshold,
            HealthStatus.Degraded,
            10);

    private static SmartPipeMetricsSnapshot Metrics(
        int inputDepth = 0,
        int outputDepth = 0,
        DateTimeOffset? lastActivity = null) => new(
            itemsProcessed: 0,
            itemsFailed: 0,
            itemsFiltered: 0,
            itemsDropped: 0,
            outputItemsDropped: 0,
            observerEventsDropped: 0,
            itemsRetried: 0,
            itemsDeadLettered: 0,
            inputDepth,
            outputDepth,
            lastStageLatencyMs: 0,
            lastProcessedAtUtc: lastActivity,
            lastActivityAtUtc: lastActivity,
            duplicatesFiltered: 0,
            avgLatencyMs: 0,
            smoothLatencyMs: 0,
            smoothThroughput: 0,
            queueSize: inputDepth,
            poolHitRate: 0);

    private sealed class ThrowingSource(Exception error) : ISmartPipeRunObservationSource
    {
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey) => throw error;
        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => throw error;
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
}
