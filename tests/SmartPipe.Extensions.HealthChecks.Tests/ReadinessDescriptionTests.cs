using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks.Tests;

public sealed class ReadinessDescriptionTests
{
    [Fact]
    public void NonHealthyDescriptionIdentifiesTheSelectedReadinessRuleWithoutRunIdentifiers()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var runId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var cases = new[]
        {
            ("active run required", new SmartPipePipelineObservation
            {
                PipelineKey = new PipelineKey("orders"),
                CapturedAtUtc = now,
                ActiveRuns = [],
                LatestTerminal = null,
            },
            new SmartPipeReadinessOptionsSnapshotForTest(SmartPipeReadinessRunRequirement.ActiveRunRequired)),
            ("non-running state", Observation(
                "orders",
                [Run("orders", PipelineRunState.Draining, runId, now.AddMinutes(-10))]),
            new SmartPipeReadinessOptionsSnapshotForTest(SmartPipeReadinessRunRequirement.ActiveRunRequired)),
            ("initial activity", Observation(
                "orders",
                [Run("orders", PipelineRunState.Running, runId, now.AddMinutes(-10))]),
            new SmartPipeReadinessOptionsSnapshotForTest(
                SmartPipeReadinessRunRequirement.ActiveRunRequired,
                RequireInitialActivity: true,
                InitialActivityGracePeriod: TimeSpan.FromMinutes(1))),
            ("queue pressure", Observation(
                "orders",
                [Run(
                    "orders",
                    PipelineRunState.Running,
                    runId,
                    now.AddMinutes(-10),
                    new SmartPipeMetricsSnapshot(
                        itemsProcessed: 0,
                        itemsFailed: 0,
                        itemsFiltered: 0,
                        itemsDropped: 0,
                        outputItemsDropped: 0,
                        observerEventsDropped: 0,
                        itemsRetried: 0,
                        itemsDeadLettered: 0,
                        inputQueueDepth: 6,
                        outputQueueDepth: 0,
                        lastStageLatencyMs: 0,
                        lastProcessedAtUtc: null,
                        lastActivityAtUtc: null,
                        duplicatesFiltered: 0,
                        avgLatencyMs: 0,
                        smoothLatencyMs: 0,
                        smoothThroughput: 0,
                        queueSize: 6,
                        poolHitRate: 0))]),
            new SmartPipeReadinessOptionsSnapshotForTest(
                SmartPipeReadinessRunRequirement.ActiveRunRequired,
                QueueUtilizationDegradedThreshold: 0.5)),
        };

        foreach (var (rule, observation, options) in cases)
        {
            var result = new SmartPipeReadinessPolicy().Evaluate(
                observation,
                options.ToSnapshot(),
                now,
                HealthStatus.Unhealthy);

            Assert.NotEqual(HealthStatus.Healthy, result.Status);
            Assert.Contains(rule, result.Description, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.Description.Length <= 256);
            Assert.DoesNotContain(runId.ToString("D"), result.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static SmartPipePipelineObservation Observation(
        string key,
        IReadOnlyList<SmartPipeRunSnapshot> active) => new()
    {
        PipelineKey = new PipelineKey(key),
        CapturedAtUtc = DateTimeOffset.UnixEpoch,
        ActiveRuns = active,
        LatestTerminal = null,
    };

    private static SmartPipeRunSnapshot Run(
        string key,
        PipelineRunState state,
        Guid runId,
        DateTimeOffset startedAtUtc,
        SmartPipeMetricsSnapshot? metrics = null) => new()
    {
        Identity = new SmartPipeRunIdentity { PipelineKey = new PipelineKey(key), RunId = runId },
        InputType = typeof(int),
        OutputType = typeof(int),
        StartedAtUtc = startedAtUtc,
        State = state,
        Metrics = metrics ?? SmartPipeMetricsSnapshot.Empty,
        InputCapacity = 10,
        OutputCapacity = 10,
    };

    private sealed record SmartPipeReadinessOptionsSnapshotForTest(
        SmartPipeReadinessRunRequirement RunRequirement,
        bool RequireInitialActivity = false,
        TimeSpan InitialActivityGracePeriod = default,
        double? QueueUtilizationDegradedThreshold = null)
    {
        internal SmartPipeReadinessOptionsSnapshot ToSnapshot() => new(
            RunRequirement,
            FailOnLatestFailure: true,
            RequireInitialActivity,
            InitialActivityGracePeriod == default ? TimeSpan.FromMinutes(1) : InitialActivityGracePeriod,
            StaleAfter: null,
            InitialActivityStatus: HealthStatus.Unhealthy,
            StaleActivityStatus: HealthStatus.Degraded,
            QueueUtilizationDegradedThreshold,
            QueuePressureStatus: HealthStatus.Degraded,
            MaximumReportedProblemRuns: 10);
    }
}
