using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace SmartPipe.Core;

/// <summary>OpenTelemetry-compatible metrics with export to JSON and Prometheus.</summary>
public class SmartPipeMetrics
{
    private static readonly Meter Meter = new("SmartPipe.Core", "1.0.4");
    private static readonly Counter<long> ItemsProcessedCounter = Meter.CreateCounter<long>("smartpipe.items.processed", "items");
    private static readonly Counter<long> ItemsFailedCounter = Meter.CreateCounter<long>("smartpipe.items.failed", "items");
    private static readonly Counter<long> DuplicatesFilteredCounter = Meter.CreateCounter<long>("smartpipe.duplicates.filtered", "items");
    private static readonly Counter<long> RetriesCounter = Meter.CreateCounter<long>("smartpipe.retries", "retries");
    private static readonly Histogram<double> LatencyHistogram = Meter.CreateHistogram<double>("smartpipe.latency", "ms");

    public long ItemsProcessed;
    public long ItemsFailed;
    public long DuplicatesFiltered;
    public long Retries;
    public double AvgLatencyMs;
    public double SmoothLatencyMs;
    public double SmoothThroughput;
    public int QueueSize;
    public double PoolHitRate;

    public void RecordProcessed(double latencyMs)
    {
        Interlocked.Increment(ref ItemsProcessed);
        ItemsProcessedCounter.Add(1);
        LatencyHistogram.Record(latencyMs);
        double total = ItemsProcessed + ItemsFailed;
        AvgLatencyMs = ((AvgLatencyMs * Math.Max(0, total - 1)) + latencyMs) / Math.Max(1, total);
    }

    public void RecordFailed() { Interlocked.Increment(ref ItemsFailed); ItemsFailedCounter.Add(1); }
    public void RecordDuplicate() { Interlocked.Increment(ref DuplicatesFiltered); DuplicatesFilteredCounter.Add(1); }
    public void RecordRetry() { Interlocked.Increment(ref Retries); RetriesCounter.Add(1); }

    /// <summary>Export all metrics as a dictionary.</summary>
    public Dictionary<string, object> Export() => new()
    {
        ["items_processed"] = ItemsProcessed,
        ["items_failed"] = ItemsFailed,
        ["duplicates_filtered"] = DuplicatesFiltered,
        ["retries"] = Retries,
        ["avg_latency_ms"] = AvgLatencyMs,
        ["smooth_latency_ms"] = SmoothLatencyMs,
        ["smooth_throughput"] = SmoothThroughput,
        ["queue_size"] = QueueSize,
        ["pool_hit_rate"] = PoolHitRate,
    };

    /// <summary>Export as JSON string.</summary>
    public string ExportJson() => JsonSerializer.Serialize(Export());

    /// <summary>Export in Prometheus text format.</summary>
    public string ExportPrometheus()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in Export())
            sb.AppendLine($"smartpipe_{key} {value}");
        return sb.ToString();
    }
}
