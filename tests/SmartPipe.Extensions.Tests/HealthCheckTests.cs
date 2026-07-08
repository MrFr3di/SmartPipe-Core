#nullable enable

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Tests;

[Trait("Category", "CorrectnessRegression")]
public sealed class HealthCheckTests
{
    [Fact]
    public async Task HealthCheck_NotStarted_Degraded()
    {
        var monitor = CreateMonitor();
        var healthCheck = CreateHealthCheck(monitor);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["state"].Should().Be(PipelineRunState.NotStarted.ToString());
    }

    [Fact]
    public async Task HealthCheck_Running_Healthy()
    {
        var monitor = CreateMonitor();
        var recorder = new SmartPipeMetricsRecorder();
        recorder.RecordProcessed(1.5);
        monitor.Track(() => PipelineRunState.Running, recorder.CaptureSnapshot);
        var healthCheck = CreateHealthCheck(monitor);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["items_failed"].Should().Be(0L);
    }

    [Fact]
    public async Task HealthCheck_Faulted_Unhealthy()
    {
        var monitor = CreateMonitor();
        monitor.Track(() => PipelineRunState.Faulted, () => SmartPipeMetricsSnapshot.Empty);
        var healthCheck = CreateHealthCheck(monitor);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task HealthCheck_QueueHigh_Degraded()
    {
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            InputCapacity = 10,
            OutputCapacity = 20,
        });
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(inputQueueDepth: 8, outputQueueDepth: 0));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            QueueUtilizationDegradedThreshold = 0.80,
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["input_queue_depth"].Should().Be(8);
        result.Data["input_capacity"].Should().Be(10);
    }

    [Fact]
    public async Task HealthCheck_Stale_Degraded()
    {
        var monitor = CreateMonitor();
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(lastProcessedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            StaleAfter = TimeSpan.FromSeconds(30),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["last_processed_at_utc"].Should().NotBe(string.Empty);
    }

    [Fact]
    public async Task HealthCheck_UsesConfiguredTimeProviderForStalePolicy()
    {
        var runtimeNow = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var healthNow = runtimeNow.AddHours(1);
        var runtimeTimeProvider = new ManualTimeProvider(runtimeNow);
        var healthTimeProvider = new ManualTimeProvider(healthNow);
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(runtimeTimeProvider),
        });
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(lastActivityAtUtc: runtimeNow));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = healthTimeProvider,
            StaleAfter = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task HealthCheck_TimeProviderBeforeLastActivity_DoesNotReportStale()
    {
        var lastActivity = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var healthNow = lastActivity.AddMinutes(-1);
        var monitor = CreateMonitor();
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(lastActivityAtUtc: lastActivity));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = new ManualTimeProvider(healthNow),
            StaleAfter = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task HealthCheck_InitialActivity_UsesConfiguredTimeProvider()
    {
        var runtimeNow = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var healthNow = runtimeNow.AddHours(1);
        var runtimeTimeProvider = new ManualTimeProvider(runtimeNow);
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(runtimeTimeProvider),
        });
        monitor.Track(() => PipelineRunState.Running, () => SmartPipeMetricsSnapshot.Empty);
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = new ManualTimeProvider(healthNow),
            RequireInitialActivity = true,
            InitialActivityGracePeriod = TimeSpan.FromMinutes(5),
            StaleAfter = TimeSpan.FromHours(2),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("initial activity");
    }

    [Fact]
    public async Task HealthCheck_CapturesTimeProviderNowOncePerCheck()
    {
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var countingTimeProvider = new CountingTimeProvider(now);
        var monitor = CreateMonitor();
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(lastActivityAtUtc: now));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = countingTimeProvider,
            StaleAfter = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        countingTimeProvider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task HealthCheck_ExactlyAtStaleThreshold_RemainsHealthy()
    {
        var lastActivity = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var monitor = CreateMonitor();
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(lastActivityAtUtc: lastActivity));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = new ManualTimeProvider(lastActivity.AddMinutes(5)),
            StaleAfter = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task HealthCheck_AfterStaleThreshold_IsDegraded()
    {
        var lastActivity = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var monitor = CreateMonitor();
        monitor.Track(
            () => PipelineRunState.Running,
            () => CreateMetrics(lastActivityAtUtc: lastActivity));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = new ManualTimeProvider(lastActivity.AddMinutes(5).AddTicks(1)),
            StaleAfter = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task HealthCheck_ExactlyAtInitialGraceBoundary_RemainsHealthy()
    {
        var started = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(new ManualTimeProvider(started)),
        });
        monitor.Track(() => PipelineRunState.Running, () => SmartPipeMetricsSnapshot.Empty);
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = new ManualTimeProvider(started.AddMinutes(5)),
            RequireInitialActivity = true,
            InitialActivityGracePeriod = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task HealthCheck_AfterInitialGraceBoundary_IsDegraded()
    {
        var started = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(new ManualTimeProvider(started)),
        });
        monitor.Track(() => PipelineRunState.Running, () => SmartPipeMetricsSnapshot.Empty);
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = new ManualTimeProvider(started.AddMinutes(5).AddTicks(1)),
            RequireInitialActivity = true,
            InitialActivityGracePeriod = TimeSpan.FromMinutes(5),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task HealthCheck_RunningWithoutActivity_DefaultPolicyRemainsHealthy()
    {
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(timeProvider),
        });
        monitor.Track(() => PipelineRunState.Running, () => SmartPipeMetricsSnapshot.Empty);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            TimeProvider = timeProvider,
            StaleAfter = TimeSpan.FromSeconds(30),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["started_at_utc"].Should().NotBe(string.Empty);
        result.Data["last_activity_at_utc"].Should().Be(string.Empty);
    }

    [Fact]
    public async Task HealthCheck_RequireInitialActivity_DegradedAfterGraceWithoutActivity()
    {
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var monitor = CreateMonitor(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(timeProvider),
        });
        monitor.Track(() => PipelineRunState.Running, () => SmartPipeMetricsSnapshot.Empty);
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        var healthCheck = CreateHealthCheck(monitor, new SmartPipeHealthCheckOptions
        {
            RequireInitialActivity = true,
            InitialActivityGracePeriod = TimeSpan.FromMinutes(1),
            TimeProvider = timeProvider,
            StaleAfter = TimeSpan.FromMinutes(10),
        });

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("initial activity");
    }

    [Fact]
    public async Task DI_FactoryStart_UpdatesHealthMonitorWithTypedRunState()
    {
        var services = new ServiceCollection();
        services.AddScoped<SingleItemSource>();
        services.AddScoped<GuidStage>();
        services.AddScoped<NullGuidSink>();
        services.AddSmartPipe<int, Guid>(
            "health-di",
            builder => builder
                .UseSource<SingleItemSource>()
                .UseStage<GuidStage>()
                .UseSink<NullGuidSink>());

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var monitor = provider.GetRequiredService<ISmartPipeRunHealthMonitor<int, Guid>>();

        await factory.Start().Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = monitor.CaptureSnapshot();
        snapshot.State.Should().Be(PipelineRunState.Completed);
        snapshot.Metrics.ItemsProcessed.Should().Be(1);
        snapshot.StartedAtUtc.Should().NotBeNull();
        snapshot.LastActivityAtUtc.Should().NotBeNull();
        snapshot.PipelineId.Should().Be("health-di");
    }

    [Fact]
    public async Task HostedService_FaultBehaviorMarkUnhealthy_HealthCheckReportsUnhealthy()
    {
        var services = new ServiceCollection();
        services.AddScoped<FaultingSource>();
        services.AddScoped<GuidStage>();
        services.AddScoped<NullGuidSink>();
        services.AddSmartPipe<int, Guid>(
            "hosted-health",
            builder => builder
                .UseSource<FaultingSource>()
                .UseStage<GuidStage>()
                .UseSink<NullGuidSink>());

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = provider.GetRequiredService<ISmartPipeFactory<int, Guid>>();
        var monitor = provider.GetRequiredService<ISmartPipeRunHealthMonitor<int, Guid>>();
        var hostedService = new ExposedHostedService<int, Guid>(
            factory,
            new SmartPipeHostedServiceOptions
            {
                FailureBehavior = SmartPipeHostedFailureBehavior.MarkUnhealthyAndKeepHostAlive,
            });

        await hostedService.ExecuteForTestAsync(CancellationToken.None);

        var healthCheck = new SmartPipeHealthCheck<int, Guid>(
            monitor,
            Options.Create(new SmartPipeHealthCheckOptions()));
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["state"].Should().Be(PipelineRunState.Faulted.ToString());
    }

    private static SmartPipeRunHealthMonitor<int, int> CreateMonitor(
        PipelineRuntimeOptions? options = null) =>
        new("health", options ?? new PipelineRuntimeOptions());

    private static SmartPipeHealthCheck<int, int> CreateHealthCheck(
        ISmartPipeRunHealthMonitor<int, int> monitor,
        SmartPipeHealthCheckOptions? options = null) =>
        new(monitor, Options.Create(options ?? new SmartPipeHealthCheckOptions()));

    private static SmartPipeMetricsSnapshot CreateMetrics(
        int inputQueueDepth = 0,
        int outputQueueDepth = 0,
        DateTimeOffset? lastProcessedAtUtc = null,
        DateTimeOffset? lastActivityAtUtc = null) =>
        new(
            itemsProcessed: lastProcessedAtUtc is null ? 0 : 1,
            itemsFailed: 0,
            itemsFiltered: 0,
            itemsDropped: 0,
            outputItemsDropped: 0,
            observerEventsDropped: 0,
            itemsRetried: 0,
            itemsDeadLettered: 0,
            inputQueueDepth,
            outputQueueDepth,
            lastStageLatencyMs: 1,
            lastProcessedAtUtc,
            lastActivityAtUtc ?? lastProcessedAtUtc,
            duplicatesFiltered: 0,
            avgLatencyMs: 1,
            smoothLatencyMs: 1,
            smoothThroughput: 1,
            queueSize: inputQueueDepth,
            poolHitRate: 0);

    private sealed class SingleItemSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return ProcessingEnvelope<int>.Create(1, "health-di", "run", 1);
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
            throw new InvalidOperationException("source failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GuidStage : IPipelineTransformer<int, Guid>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<Guid>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<Guid>.Success(Guid.NewGuid()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullGuidSink : IPipelineSink<Guid>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<Guid> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExposedHostedService<TInput, TOutput>
        : SmartPipeHostedService<TInput, TOutput>
    {
        public ExposedHostedService(
            ISmartPipeFactory<TInput, TOutput> factory,
            SmartPipeHostedServiceOptions options)
            : base(
                factory,
                NullLogger<SmartPipeHostedService<TInput, TOutput>>.Instance,
                Options.Create(options))
        {
        }

        public Task ExecuteForTestAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
        }
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public CountingTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public int Calls { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Calls++;
            return _utcNow;
        }
    }
}
