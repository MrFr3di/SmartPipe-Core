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
        export.Should().ContainKey("duplicates_filtered");
        export.Should().ContainKey("retries");
        export.Should().ContainKey("avg_latency_ms");
        export.Should().ContainKey("smooth_latency_ms");
        export.Should().ContainKey("smooth_throughput");
        export.Should().ContainKey("queue_size");
        export.Should().ContainKey("pool_hit_rate");
    }

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
    public void ExportPrometheus_ShouldContainMetrics()
    {
        var metrics = new SmartPipeMetrics();
        metrics.RecordProcessed(5.0);
        var prom = metrics.ExportPrometheus();
        prom.Should().Contain("smartpipe_items_processed");
    }
}
