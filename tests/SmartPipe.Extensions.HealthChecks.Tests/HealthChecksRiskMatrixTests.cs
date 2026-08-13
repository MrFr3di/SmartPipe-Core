using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks.Tests;

public sealed class HealthChecksRiskMatrixTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void LivenessProblemRunLimitMatrix(int limit, bool valid) =>
        Assert.Equal(valid, SmartPipeLivenessOptionsValidator.ValidateLimit(limit, nameof(SmartPipeLivenessOptions.MaximumReportedProblemRuns)).Succeeded);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ReadinessProblemRunLimitMatrix(int limit, bool valid) =>
        Assert.Equal(valid, SmartPipeReadinessOptionsValidator.Validate(new SmartPipeReadinessOptions
        {
            MaximumReportedProblemRuns = limit,
        }).Count == 0);

    [Theory]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, null, true)]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, SmartPipeRunObservationOutcome.Completed, true)]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, SmartPipeRunObservationOutcome.Cancelled, true)]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, SmartPipeRunObservationOutcome.Aborted, true)]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, SmartPipeRunObservationOutcome.Faulted, false)]
    [InlineData(SmartPipeReadinessRunRequirement.RegistrationOnly, SmartPipeRunObservationOutcome.ActivationFailed, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveRunRequired, null, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveRunRequired, SmartPipeRunObservationOutcome.Completed, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Completed, true)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Cancelled, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Aborted, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.Faulted, false)]
    [InlineData(SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion, SmartPipeRunObservationOutcome.ActivationFailed, false)]
    public void ReadinessRequirementAndLatestOutcomeMatrix(
        SmartPipeReadinessRunRequirement requirement,
        SmartPipeRunObservationOutcome? outcome,
        bool healthy)
    {
        var result = new SmartPipeReadinessPolicy().Evaluate(
            Observation("orders", terminal: outcome is null ? null : Terminal("orders", outcome.Value)),
            Readiness(requirement),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.Equal(healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, result.Status);
    }

    [Theory]
    [InlineData(PipelineRunState.NotStarted)]
    [InlineData(PipelineRunState.Draining)]
    [InlineData(PipelineRunState.Completed)]
    [InlineData(PipelineRunState.Cancelled)]
    [InlineData(PipelineRunState.Aborted)]
    [InlineData(PipelineRunState.Faulted)]
    public void ActiveNonRunningStatesAreReadinessHardFailures(PipelineRunState state)
    {
        var result = new SmartPipeReadinessPolicy().Evaluate(
            Observation("orders", [Run("orders", state)]),
            Readiness(),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void LivenessIgnoresQueueAndStaleSignalsForRunningRun()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var run = Run("orders", PipelineRunState.Running, metrics: Metrics(
            inputDepth: 10,
            outputDepth: 10,
            lastActivity: now.AddHours(-1)));

        var result = new SmartPipeLivenessPolicy().Evaluate(
            Observation("orders", [run]),
            new(true, true, 10),
            now,
            HealthStatus.Unhealthy);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void MultipleRunsAggregateWorstStatusAndBoundReportedProblems()
    {
        var now = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var healthy = Run("orders", PipelineRunState.Running, metrics: Metrics(lastActivity: now));
        var degraded = Run("orders", PipelineRunState.Running, metrics: Metrics(inputDepth: 5, lastActivity: now));
        var unhealthy = Run("orders", PipelineRunState.Running, started: now.AddMinutes(-5));
        var options = Readiness(queueThreshold: 0.5, staleAfter: TimeSpan.FromMinutes(1), requireActivity: true) with
        {
            MaximumReportedProblemRuns = 1,
        };

        var result = new SmartPipeReadinessPolicy().Evaluate(
            Observation("orders", [healthy, degraded, unhealthy]),
            options,
            now,
            HealthStatus.Unhealthy);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(2, result.Data["smartpipe.problem_run_count"]);
        Assert.Equal(1, result.Data["smartpipe.problem_runs_reported"]);
        Assert.Equal(true, result.Data["smartpipe.problem_runs_truncated"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(32)]
    public void MultiRunCardinalityMatrixIsCapturedAsOneImmutableObservation(int count)
    {
        var runs = Enumerable.Range(0, count)
            .Select(_ => Run("orders", PipelineRunState.Running))
            .ToArray();

        var result = new SmartPipeLivenessPolicy().Evaluate(
            Observation("orders", runs),
            new(true, true, 10),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.Equal(count, result.Data["smartpipe.active_run_count"]);
    }

    [Fact]
    public void ObservationCopiesActiveRunListBeforeEvaluation()
    {
        var runs = new List<SmartPipeRunSnapshot> { Run("orders", PipelineRunState.Running) };
        var observation = Observation("orders", runs);
        runs.Clear();

        var result = new SmartPipeLivenessPolicy().Evaluate(
            observation,
            new(true, true, 10),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.Equal(1, result.Data["smartpipe.active_run_count"]);
    }

    [Fact]
    public void DuplicateActiveRunIdentityIsRejected()
    {
        var run = Run("orders", PipelineRunState.Running);
        var duplicate = run with { };

        Assert.Throws<InvalidOperationException>(() => new SmartPipeLivenessPolicy().Evaluate(
            Observation("orders", [run, duplicate]),
            new(true, true, 10),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy));
    }

    [Fact]
    public void FailedRegistrationRollsBackDescriptorsAndReservation()
    {
        var services = new ServiceCollection();
        var optionsName = "rollback";

        Assert.Throws<InvalidOperationException>(() => SmartPipeHealthCheckRegistrationExtensions.Register(
            services,
            optionsName,
            null,
            [],
            null,
            _ => throw new InvalidOperationException("configure failed"),
            _ => new ThrowingHealthCheck()));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SmartPipeHealthCheckRegistrationStore));

        SmartPipeHealthCheckRegistrationExtensions.Register(
            services,
            optionsName,
            null,
            [],
            null,
            static _ => { },
            _ => new ThrowingHealthCheck());

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(SmartPipeHealthCheckRegistrationStore));
    }

    [Fact]
    public void CustomNameCollisionIsOrdinalAcrossCheckKinds()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(Definition("orders"));
        registration.AddLiveness(name: "custom");

        Assert.Throws<InvalidOperationException>(() => registration.AddReadiness(name: "custom"));
        Assert.Throws<InvalidOperationException>(() => registration.AddLiveness(name: "custom"));
    }

    [Fact]
    public void CustomNamesAreIsolatedPerNamedOptionsAndDuplicateGlobally()
    {
        var services = new ServiceCollection();
        var orders = services.AddSmartPipe().AddPipeline(Definition("orders"));
        var replay = services.AddSmartPipe().AddPipeline(Definition("replay"));
        orders.AddLiveness(options => options.FailOnLatestFault = false, name: "orders-live");
        replay.AddLiveness(options => options.FailOnLatestFault = true, name: "replay-live");
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>();

        Assert.False(monitor.Get("orders-live").FailOnLatestFault);
        Assert.True(monitor.Get("replay-live").FailOnLatestFault);
        Assert.Throws<InvalidOperationException>(() => replay.AddReadiness(name: "orders-live"));
    }

    [Fact]
    public void ValidateOnStartUsesOneStartupValidatorDescriptor()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(Definition("orders"));
        registration.AddLiveness();
        registration.AddReadiness();

        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IStartupValidator)));
    }

    [Fact]
    public void NegativeTimeoutIsRejected()
    {
        var registration = new ServiceCollection().AddSmartPipe().AddPipeline(Definition("orders"));

        Assert.Throws<ArgumentOutOfRangeException>(() => registration.AddLiveness(timeout: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void NullTagIsRejectedAtRegistrationBoundary()
    {
        var registration = new ServiceCollection().AddSmartPipe().AddPipeline(Definition("orders"));

        Assert.Throws<ArgumentException>(() => registration.AddLiveness(tags: new[] { (string)null! }));
    }

    [Fact]
    public void ConfigureDelegateRunsOncePerNamedMaterialization()
    {
        var services = new ServiceCollection();
        var calls = 0;
        services.AddSmartPipe().AddPipeline(Definition("orders"))
            .AddLiveness(_ => Interlocked.Increment(ref calls));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>();
        var name = SmartPipeHealthCheckNames.Liveness(new PipelineKey("orders"));

        _ = monitor.Get(name);
        _ = monitor.Get(name);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OptionsRefreshAffectsLaterCheckButNotInFlightSnapshot()
    {
        var services = new ServiceCollection();
        var key = new PipelineKey("orders");
        var source = new GateSource(Observation("orders", terminal: Terminal("orders", SmartPipeRunObservationOutcome.Faulted)));
        services.AddSingleton<ISmartPipeRunObservationSource>(source);
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<SmartPipeLivenessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var monitor = new MutableOptionsMonitor<SmartPipeLivenessOptions>(new SmartPipeLivenessOptions
        {
            FailOnLatestFault = true,
        });
        var check = new SmartPipePipelineLivenessHealthCheck(key, source, monitor, TimeProvider.System);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("probe", check, HealthStatus.Unhealthy, null),
        };

        var inFlight = Task.Run(() => check.CheckHealthAsync(context, TestContext.Current.CancellationToken));
        await source.Captured.Task.WaitAsync(TestContext.Current.CancellationToken);
        monitor.Set(new SmartPipeLivenessOptions { FailOnLatestFault = false });
        source.Release();
        var first = await inFlight;
        var second = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, first.Status);
        Assert.Equal(HealthStatus.Healthy, second.Status);
    }

    [Fact]
    public void ReadinessValidatorRejectsUndefinedAndInvalidBoundaries()
    {
        var invalid = SmartPipeReadinessOptionsValidator.Validate(new SmartPipeReadinessOptions
        {
            RunRequirement = (SmartPipeReadinessRunRequirement)99,
            InitialActivityGracePeriod = TimeSpan.Zero,
            StaleAfter = TimeSpan.Zero,
            QueueUtilizationDegradedThreshold = double.PositiveInfinity,
            InitialActivityStatus = HealthStatus.Healthy,
            StaleActivityStatus = HealthStatus.Healthy,
            QueuePressureStatus = HealthStatus.Healthy,
            RequireInitialActivity = true,
        });

        Assert.Equal(7, invalid.Count);
        Assert.Contains(invalid, failure => failure.Contains(nameof(SmartPipeReadinessOptions.RunRequirement), StringComparison.Ordinal));
        Assert.Contains(invalid, failure => failure.Contains(nameof(SmartPipeReadinessOptions.InitialActivityGracePeriod), StringComparison.Ordinal));
        Assert.Contains(invalid, failure => failure.Contains(nameof(SmartPipeReadinessOptions.StaleAfter), StringComparison.Ordinal));
        Assert.Contains(invalid, failure => failure.Contains(nameof(SmartPipeReadinessOptions.QueueUtilizationDegradedThreshold), StringComparison.Ordinal));
    }

    [Fact]
    public void HardFailureHonorsDegradedRegistrationStatus()
    {
        var result = new SmartPipeReadinessPolicy().Evaluate(
            Observation("orders"),
            Readiness(SmartPipeReadinessRunRequirement.ActiveRunRequired),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void DataContainsOnlyAllowedPrimitiveValuesAndNoSecrets()
    {
        var result = new SmartPipeReadinessPolicy().Evaluate(
            Observation("orders", [Run("orders", PipelineRunState.Running, metrics: Metrics(inputDepth: 1))]),
            Readiness(queueThreshold: 0.5),
            DateTimeOffset.UnixEpoch,
            HealthStatus.Unhealthy);

        Assert.All(result.Data.Values, value => Assert.True(value is string or bool or int or long or double));
        Assert.DoesNotContain(result.Data.Keys, key => key.Contains("exception", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Data.Keys, key => key.Contains("payload", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Data.Keys, key => key.Contains("metadata", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Data.Keys, key => key.Contains("stack", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OverflowInAggregateDataIsSanitized()
    {
        var key = new PipelineKey("orders");
        var source = new FixedSource(Observation("orders", [Run("orders", PipelineRunState.Running, metrics: Metrics(itemsProcessed: long.MaxValue)) , Run("orders", PipelineRunState.Running, metrics: Metrics(itemsProcessed: long.MaxValue))]));
        var services = new ServiceCollection();
        services.AddOptions<SmartPipeLivenessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var check = new SmartPipePipelineLivenessHealthCheck(key, source, provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(), TimeProvider.System);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("probe", check, HealthStatus.Degraded, null) };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain(result.Data.Values, value => value is long.MaxValue);
    }

    [Fact]
    public async Task AggregateIncludeAllPreservesRegistrationOrder()
    {
        var registrations = new[] { Descriptor("b"), Descriptor("a") };
        var captured = new ConcurrentQueue<string>();
        var source = new RecordingSource(captured, key => Observation(key.Value));
        var result = await EvaluateAggregate(
            registrations,
            source,
            new SmartPipeAggregateLivenessOptions(),
            HealthStatus.Unhealthy,
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(["b", "a"], captured.ToArray());
    }

    [Fact]
    public async Task AggregateExplicitKeysFollowRegistrationOrder()
    {
        var registrations = new[] { Descriptor("a"), Descriptor("b"), Descriptor("c") };
        var captured = new ConcurrentQueue<string>();
        var source = new RecordingSource(captured, key => Observation(key.Value));
        var options = new SmartPipeAggregateLivenessOptions { IncludeAllRegisteredPipelines = false };
        options.IncludedPipelines.Add(new PipelineKey("c"));
        options.IncludedPipelines.Add(new PipelineKey("a"));

        _ = await EvaluateAggregate(registrations, source, options, HealthStatus.Unhealthy, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "c"], captured.ToArray());
    }

    [Fact]
    public async Task AggregateHundredKeysKeepsIndexedOutputBoundedAndExact()
    {
        var keys = Enumerable.Range(0, 99).Select(index => index == 0 ? "a,b" : $"k{index}").ToArray();
        var registrations = keys.Select(Descriptor).ToArray();
        var source = new RecordingSource(new ConcurrentQueue<string>(), key =>
            Observation(key.Value, terminal: Terminal(key.Value, SmartPipeRunObservationOutcome.Faulted)));
        var options = new SmartPipeAggregateLivenessOptions { MaximumReportedProblemKeys = 2 };

        var result = await EvaluateAggregate(registrations, source, options, HealthStatus.Degraded, TestContext.Current.CancellationToken);

        Assert.Equal(99, result.Data["smartpipe.pipeline_count"]);
        Assert.Equal(2, result.Data["smartpipe.problem_keys_reported"]);
        Assert.Equal(true, result.Data["smartpipe.problem_keys_truncated"]);
        Assert.Equal("a,b", result.Data["smartpipe.problem_key_0"]);
        Assert.DoesNotContain("smartpipe.problem_keys", result.Data.Keys);
        Assert.NotNull(result.Description);
        Assert.True(result.Description!.Length < 200);
    }

    [Fact]
    public async Task AggregateWorstStatusIsPreserved()
    {
        var registrations = new[] { Descriptor("healthy"), Descriptor("faulted") };
        var source = new RecordingSource(new ConcurrentQueue<string>(), key =>
            key.Value == "faulted"
                ? Observation("faulted", terminal: Terminal("faulted", SmartPipeRunObservationOutcome.Faulted))
                : Observation("healthy"));

        var result = await EvaluateAggregate(
            registrations,
            source,
            new SmartPipeAggregateLivenessOptions(),
            HealthStatus.Degraded,
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(1, result.Data["smartpipe.healthy_count"]);
        Assert.Equal(1, result.Data["smartpipe.degraded_count"]);
    }

    [Fact]
    public async Task AggregateCallerCancellationBetweenKeysEscapes()
    {
        var registrations = new[] { Descriptor("a"), Descriptor("b") };
        using var cancellation = new CancellationTokenSource();
        var source = new RecordingSource(new ConcurrentQueue<string>(), key =>
        {
            if (key.Value == "a") cancellation.Cancel();
            return Observation(key.Value);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await EvaluateAggregate(registrations, source, new SmartPipeAggregateLivenessOptions(), HealthStatus.Unhealthy, cancellation.Token));
    }

    [Fact]
    public async Task AggregateCaptureFailureContinuesWithLaterKeys()
    {
        var captured = new ConcurrentQueue<string>();
        var registrations = new[] { Descriptor("bad"), Descriptor("good") };
        var source = new RecordingSource(captured, key =>
        {
            if (key.Value == "bad") throw new InvalidOperationException("secret payload");
            return Observation("good");
        });

        var result = await EvaluateAggregate(registrations, source, new SmartPipeAggregateLivenessOptions(), HealthStatus.Degraded, TestContext.Current.CancellationToken);

        Assert.Equal(["bad", "good"], captured.ToArray());
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(2, result.Data["smartpipe.pipeline_count"]);
        Assert.DoesNotContain("secret", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateForeignCancellationIsSanitized()
    {
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var registrations = new[] { Descriptor("bad"), Descriptor("good") };
        var source = new RecordingSource(new ConcurrentQueue<string>(), key =>
        {
            if (key.Value == "bad") throw new OperationCanceledException(foreign.Token);
            return Observation("good");
        });

        var result = await EvaluateAggregate(registrations, source, new SmartPipeAggregateLivenessOptions(), HealthStatus.Degraded, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain("OperationCanceled", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateThousandKeysDescriptionAndDataRemainBounded()
    {
        var registrations = Enumerable.Range(0, 1000).Select(index => Descriptor($"key-{index}" )).ToArray();
        var source = new RecordingSource(new ConcurrentQueue<string>(), key =>
            Observation(key.Value, terminal: Terminal(key.Value, SmartPipeRunObservationOutcome.Faulted)));

        var result = await EvaluateAggregate(registrations, source, new SmartPipeAggregateLivenessOptions { MaximumReportedProblemKeys = 100 }, HealthStatus.Unhealthy, TestContext.Current.CancellationToken);

        Assert.Equal(1000, result.Data["smartpipe.pipeline_count"]);
        Assert.Equal(100, result.Data["smartpipe.problem_keys_reported"]);
        Assert.Equal(true, result.Data["smartpipe.problem_keys_truncated"]);
        Assert.Equal("key-0", result.Data["smartpipe.problem_key_0"]);
        Assert.Equal("key-99", result.Data["smartpipe.problem_key_99"]);
        Assert.NotNull(result.Description);
        Assert.True(result.Description!.Length < 200);
        Assert.True(result.Data.Count <= 110);
        Assert.All(result.Data.Values, value => Assert.True(value is string or bool or int or long or double));
    }

    [Theory]
    [InlineData(null, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Degraded)]
    public async Task NullAndDegradedFailureStatusesAreHonored(HealthStatus? configured, HealthStatus expected)
    {
        var key = new PipelineKey("orders");
        var services = new ServiceCollection();
        services.AddOptions<SmartPipeReadinessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var check = new SmartPipePipelineReadinessHealthCheck(key, new FixedSource(Observation("orders")), provider.GetRequiredService<IOptionsMonitor<SmartPipeReadinessOptions>>(), TimeProvider.System);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("probe", check, configured, null) };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task ThirtyTwoConcurrentChecksKeepEachSnapshotInternallyConsistent()
    {
        var key = new PipelineKey("orders");
        var source = new AlternatingSource();
        var services = new ServiceCollection();
        services.AddOptions<SmartPipeLivenessOptions>("probe");
        using var provider = services.BuildServiceProvider();
        var check = new SmartPipePipelineLivenessHealthCheck(key, source, provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(), TimeProvider.System);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("probe", check, HealthStatus.Unhealthy, null) };

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(async () => await check.CheckHealthAsync(context, TestContext.Current.CancellationToken))));

        Assert.All(results, result =>
        {
            Assert.Equal("orders", result.Data["smartpipe.pipeline_key"]);
            Assert.Equal("liveness", result.Data["smartpipe.check_kind"]);
            Assert.Contains((int)result.Data["smartpipe.active_run_count"], new[] { 0, 1 });
        });
    }

    private static async Task<HealthCheckResult> EvaluateAggregate(
        IReadOnlyList<SmartPipeRegistrationDescriptor> registrations,
        ISmartPipeRunObservationSource source,
        SmartPipeAggregateLivenessOptions options,
        HealthStatus failureStatus,
        CancellationToken cancellationToken)
    {
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("aggregate", new ThrowingHealthCheck(), failureStatus, null),
        };
        var check = new SmartPipeAggregateLivenessHealthCheck(
            new FakeRegistry(registrations),
            source,
            new MutableOptionsMonitor<SmartPipeAggregateLivenessOptions>(options),
            TimeProvider.System);
        return await check.CheckHealthAsync(context, cancellationToken);
    }

    private static SmartPipeRegistrationDescriptor Descriptor(string key) => new()
    {
        Key = new PipelineKey(key),
        InputType = typeof(int),
        OutputType = typeof(int),
        DefinitionType = typeof(PipelineDefinition<int, int>),
        FactoryType = typeof(object),
        DisplayName = key,
        RegistrationOrder = 0,
        IsReusable = true,
    };

    private static PipelineDefinition<int, int> Definition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
            .Build();

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

    private static SmartPipeTerminalRunObservation Terminal(string key, SmartPipeRunObservationOutcome outcome) => new()
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
        long itemsProcessed = 0,
        int inputDepth = 0,
        int outputDepth = 0,
        DateTimeOffset? lastActivity = null) => new(
            itemsProcessed,
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

    private sealed class FakeRegistry(IReadOnlyList<SmartPipeRegistrationDescriptor> registrations) : ISmartPipeRegistry
    {
        public IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations() => registrations;
        public SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key) => registrations.Single(item => item.Key == key);
        public bool TryGetRegistration(PipelineKey key, [NotNullWhen(true)] out SmartPipeRegistrationDescriptor? registration)
        {
            registration = registrations.FirstOrDefault(item => item.Key == key);
            return registration is not null;
        }
    }

    private sealed class RecordingSource(ConcurrentQueue<string> captured, Func<PipelineKey, SmartPipePipelineObservation> capture) : ISmartPipeRunObservationSource
    {
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey)
        {
            captured.Enqueue(pipelineKey.Value);
            return capture(pipelineKey);
        }

        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => [];
    }

    private sealed class FixedSource(SmartPipePipelineObservation observation) : ISmartPipeRunObservationSource
    {
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey) => observation;
        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => [observation];
    }

    private sealed class GateSource(SmartPipePipelineObservation observation) : ISmartPipeRunObservationSource
    {
        private readonly ManualResetEventSlim _release = new(false);
        public TaskCompletionSource<bool> Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey)
        {
            Captured.TrySetResult(true);
            _release.Wait();
            return observation;
        }

        public void Release() => _release.Set();
        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => [observation];
    }

    private sealed class AlternatingSource : ISmartPipeRunObservationSource
    {
        private int _count;
        public SmartPipePipelineObservation Capture(PipelineKey pipelineKey)
        {
            var index = Interlocked.Increment(ref _count);
            return index % 2 == 0
                ? Observation("orders")
                : Observation("orders", [Run("orders", PipelineRunState.Running)]);
        }

        public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() => [];
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        private T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;
        public void Set(T value) => _value = value;

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class ThrowingHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(HealthStatus.Healthy));
    }

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
