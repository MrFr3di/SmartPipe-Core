#nullable enable

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Tests;

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
        snapshot.PipelineId.Should().Be("health-di");
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
        DateTimeOffset? lastProcessedAtUtc = null) =>
        new(
            itemsProcessed: lastProcessedAtUtc is null ? 0 : 1,
            itemsFailed: 0,
            itemsRetried: 0,
            itemsDeadLettered: 0,
            inputQueueDepth,
            outputQueueDepth,
            lastStageLatencyMs: 1,
            lastProcessedAtUtc,
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
}
