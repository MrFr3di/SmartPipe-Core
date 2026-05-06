#nullable enable

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace SmartPipe.Core;

/// <summary>OpenTelemetry-compatible metrics with export to JSON and Prometheus.</summary>
public class SmartPipeMetrics
{
    private static readonly Meter Meter = new("SmartPipe.Core", "1.0.5");
    private static readonly Counter<long> ItemsProcessedCounter = Meter.CreateCounter<long>("smartpipe.items.processed", "items");
    private static readonly Counter<long> ItemsFailedCounter = Meter.CreateCounter<long>("smartpipe.items.failed", "items");
    private static readonly Counter<long> DuplicatesFilteredCounter = Meter.CreateCounter<long>("smartpipe.duplicates.filtered", "items");
    private static readonly Counter<long> RetriesCounter = Meter.CreateCounter<long>("smartpipe.retries", "retries");
    private static readonly Histogram<double> LatencyHistogram = Meter.CreateHistogram<double>("smartpipe.latency", "ms");

    /// <summary>Total items successfully processed.</summary>
    public long ItemsProcessed;

    /// <summary>Total items that failed processing.</summary>
    public long ItemsFailed;

    /// <summary>Total duplicate items filtered out.</summary>
    public long DuplicatesFiltered;

    /// <summary>Total retry attempts made.</summary>
    public long Retries;

    /// <summary>Running average latency in milliseconds.</summary>
    public double AvgLatencyMs;

    /// <summary>EMA-smoothed latency in milliseconds.</summary>
    public double SmoothLatencyMs;

    /// <summary>EMA-smoothed throughput (items/sec).</summary>
    public double SmoothThroughput;

    /// <summary>Current queue size.</summary>
    public int QueueSize;

    /// <summary>ObjectPool hit rate (0.0-1.0).</summary>
    public double PoolHitRate;

    /// <summary>Record a processed item and its latency.</summary>
    /// <param name="latencyMs">Measured latency in milliseconds.</param>
    public void RecordProcessed(double latencyMs)
    {
        Interlocked.Increment(ref ItemsProcessed);
        ItemsProcessedCounter.Add(1);
        LatencyHistogram.Record(latencyMs);
        double total = ItemsProcessed + ItemsFailed;
        AvgLatencyMs = ((AvgLatencyMs * Math.Max(0, total - 1)) + latencyMs) / Math.Max(1, total);
    }

    /// <summary>Record a failed item.</summary>
    public void RecordFailed() { Interlocked.Increment(ref ItemsFailed); ItemsFailedCounter.Add(1); }

    /// <summary>Record a filtered duplicate.</summary>
    public void RecordDuplicate() { Interlocked.Increment(ref DuplicatesFiltered); DuplicatesFilteredCounter.Add(1); }

    /// <summary>Record a retry attempt.</summary>
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
