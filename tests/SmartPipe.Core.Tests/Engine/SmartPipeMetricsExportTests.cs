using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeMetricsExportTests
{
    [Fact]
    public void Export_ShouldReturnDictionaryWithAllKeys()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordProcessed(10.0);
        metrics.RecordFailed();
        metrics.RecordDuplicate();

        var export = metrics.Export();
        export.Should().ContainKey("items_processed");
        export.Should().ContainKey("items_failed");
        export.Should().ContainKey("items_filtered");
        export.Should().ContainKey("items_dropped");
        export.Should().ContainKey("output_items_dropped");
        export.Should().ContainKey("observer_events_dropped");
        export.Should().ContainKey("duplicates_filtered");
        export.Should().ContainKey("retries");
        export.Should().ContainKey("avg_latency_ms");
        export.Should().ContainKey("smooth_latency_ms");
        export.Should().ContainKey("smooth_throughput");
        export.Should().ContainKey("queue_size");
        export.Should().ContainKey("pool_hit_rate");
    }

    [Fact]
    public void Export_ShouldPreserveOutputShape()
    {
        var metrics = new SmartPipeMetrics();
        var export = metrics.Export();

        export.Keys.Should().BeEquivalentTo(
        [
            "items_processed",
            "items_failed",
            "items_filtered",
            "items_dropped",
            "output_items_dropped",
            "observer_events_dropped",
            "duplicates_filtered",
            "retries",
            "items_dead_lettered",
            "avg_latency_ms",
            "last_stage_latency_ms",
            "smooth_latency_ms",
            "smooth_throughput",
            "queue_size",
            "input_queue_depth",
            "output_queue_depth",
            "pool_hit_rate",
            "last_processed_at_utc",
        ]);
    }

    [Fact]
    public void SmartPipeMetricsSnapshot_Export_ShouldMatchSampledView()
    {
        var metrics = new SmartPipeMetrics();
        metrics.UpdateQueueSize(7);
        metrics.RecordPoolHitRate(0.5);

        metrics.RecordProcessed(25.0);
        metrics.RecordRetry();

        var export = metrics.CaptureSnapshot().Export();

        export["items_processed"].Should().Be(1L);
        export["retries"].Should().Be(1L);
        export["avg_latency_ms"].Should().Be(25.0);
        export["queue_size"].Should().Be(7);
        export["pool_hit_rate"].Should().Be(0.5);
    }

    [Fact]
    public async Task CaptureSnapshot_Export_ShouldNotThrowDuringConcurrentUpdates()
    {
        var metrics = new SmartPipeMetrics();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                for (int i = 0; i < 10_000; i++)
                {
                    metrics.RecordProcessed(1.0);
                    metrics.RecordFailed();
                    metrics.RecordDuplicate();
                    metrics.RecordRetry();
                    metrics.UpdateQueueSize(i);
                    metrics.RecordPoolHitRate(0.5);
                }
            }))
            .ToArray();

        var exports = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return Enumerable.Range(0, 250)
                    .Select(_ => metrics.CaptureSnapshot().Export())
                    .ToArray();
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(updates.Concat<Task>(exports)).WaitAsync(TimeSpan.FromSeconds(5));

        exports
            .SelectMany(task => task.Result)
            .Should()
            .OnlyContain(export => HasExpectedShape(export));
    }

    private static bool HasExpectedShape(Dictionary<string, object> export) =>
        export.Count == 18
        && export.TryGetValue("items_processed", out var itemsProcessed) && itemsProcessed is long
        && export.TryGetValue("items_failed", out var itemsFailed) && itemsFailed is long
        && export.TryGetValue("items_filtered", out var itemsFiltered) && itemsFiltered is long
        && export.TryGetValue("items_dropped", out var itemsDropped) && itemsDropped is long
        && export.TryGetValue("output_items_dropped", out var outputItemsDropped) && outputItemsDropped is long
        && export.TryGetValue("observer_events_dropped", out var observerEventsDropped) && observerEventsDropped is long
        && export.TryGetValue("duplicates_filtered", out var duplicatesFiltered) && duplicatesFiltered is long
        && export.TryGetValue("retries", out var retries) && retries is long
        && export.TryGetValue("items_dead_lettered", out var itemsDeadLettered) && itemsDeadLettered is long
        && export.TryGetValue("avg_latency_ms", out var avgLatencyMs) && avgLatencyMs is double
        && export.TryGetValue("last_stage_latency_ms", out var lastStageLatencyMs) && lastStageLatencyMs is double
        && export.TryGetValue("smooth_latency_ms", out var smoothLatencyMs) && smoothLatencyMs is double
        && export.TryGetValue("smooth_throughput", out var smoothThroughput) && smoothThroughput is double
        && export.TryGetValue("queue_size", out var queueSize) && queueSize is int
        && export.TryGetValue("input_queue_depth", out var inputQueueDepth) && inputQueueDepth is int
        && export.TryGetValue("output_queue_depth", out var outputQueueDepth) && outputQueueDepth is int
        && export.TryGetValue("pool_hit_rate", out var poolHitRate) && poolHitRate is double
        && export.TryGetValue("last_processed_at_utc", out var lastProcessedAtUtc) && lastProcessedAtUtc is string;

    [Fact]
    public void ExportJson_ShouldReturnValidJson()
    {
        var metrics = new SmartPipeMetrics();
        var json = metrics.ExportJson();
        json.Should().StartWith("{");
        json.Should().EndWith("}");
        json.Should().Contain("items_processed");
    }

    [Fact]
    public void ToDiagnosticText_ShouldContainMetrics()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordProcessed(5.0);
        var diagnosticText = metrics.ToDiagnosticText();
        diagnosticText.Should().Contain("smartpipe_items_processed");
    }

    [Fact]
    public void ExportPrometheus_ShouldDelegateToDiagnosticText()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordProcessed(5.0);
#pragma warning disable CS0618
        var compatibilityText = metrics.ExportPrometheus();
#pragma warning restore CS0618

        compatibilityText.Should().Be(metrics.ToDiagnosticText());
    }
}
