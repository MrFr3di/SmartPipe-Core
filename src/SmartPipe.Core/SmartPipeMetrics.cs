#nullable enable

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;

namespace SmartPipe.Core;

/// <summary>
/// Observational, point-in-time sample of <see cref="SmartPipeMetrics"/> values.
/// </summary>
/// <remarks>
/// The snapshot is safe for export and reporting, but it is not a transactional synchronization
/// primitive and does not coordinate concurrent pipeline updates.
/// </remarks>
public sealed class SmartPipeMetricsSnapshot
{
    internal SmartPipeMetricsSnapshot(
        long itemsProcessed,
        long itemsFailed,
        long duplicatesFiltered,
        long retries,
        double avgLatencyMs,
        double smoothLatencyMs,
        double smoothThroughput,
        int queueSize,
        double poolHitRate)
    {
        ItemsProcessed = itemsProcessed;
        ItemsFailed = itemsFailed;
        DuplicatesFiltered = duplicatesFiltered;
        Retries = retries;
        AvgLatencyMs = avgLatencyMs;
        SmoothLatencyMs = smoothLatencyMs;
        SmoothThroughput = smoothThroughput;
        QueueSize = queueSize;
        PoolHitRate = poolHitRate;
    }

    /// <summary>Total items successfully processed in the sampled view.</summary>
    public long ItemsProcessed { get; }

    /// <summary>Total items that failed processing in the sampled view.</summary>
    public long ItemsFailed { get; }

    /// <summary>Total duplicate items filtered out in the sampled view.</summary>
    public long DuplicatesFiltered { get; }

    /// <summary>Total retry attempts made in the sampled view.</summary>
    public long Retries { get; }

    /// <summary>Running average latency in milliseconds in the sampled view.</summary>
    public double AvgLatencyMs { get; }

    /// <summary>EMA-smoothed latency in milliseconds in the sampled view.</summary>
    public double SmoothLatencyMs { get; }

    /// <summary>EMA-smoothed throughput in items per second in the sampled view.</summary>
    public double SmoothThroughput { get; }

    /// <summary>Current queue size in the sampled view.</summary>
    public int QueueSize { get; }

    /// <summary>ObjectPool hit rate in the sampled view.</summary>
    public double PoolHitRate { get; }

    /// <summary>Export the sampled values as a dictionary.</summary>
    public Dictionary<string, object> Export() =>
        new()
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
}

/// <summary>OpenTelemetry-compatible metrics with export to JSON and Prometheus.</summary>
public class SmartPipeMetrics
{
    private static readonly Meter Meter = new(
        "SmartPipe.Core",
        typeof(SmartPipeMetrics).Assembly.GetName().Version?.ToString() ?? "1.0.0"
    );
    private static readonly Counter<long> ItemsProcessedCounter = Meter.CreateCounter<long>(
        "smartpipe.items.processed",
        "items"
    );
    private static readonly Counter<long> ItemsFailedCounter = Meter.CreateCounter<long>(
        "smartpipe.items.failed",
        "items"
    );
    private static readonly Counter<long> DuplicatesFilteredCounter = Meter.CreateCounter<long>(
        "smartpipe.duplicates.filtered",
        "items"
    );
    private static readonly Counter<long> RetriesCounter = Meter.CreateCounter<long>(
        "smartpipe.retries",
        "retries"
    );
    private static readonly Histogram<double> LatencyHistogram = Meter.CreateHistogram<double>(
        "smartpipe.latency",
        "ms"
    );

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

    /// <summary>ObjectPool hit rate (0.0-1.0). Updated externally by the pipeline when context pool is used.</summary>
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
    public void RecordFailed()
    {
        Interlocked.Increment(ref ItemsFailed);
        ItemsFailedCounter.Add(1);
    }

    /// <summary>Record a filtered duplicate.</summary>
    public void RecordDuplicate()
    {
        Interlocked.Increment(ref DuplicatesFiltered);
        DuplicatesFilteredCounter.Add(1);
    }

    /// <summary>Record a retry attempt.</summary>
    public void RecordRetry()
    {
        Interlocked.Increment(ref Retries);
        RetriesCounter.Add(1);
    }

    /// <summary>Update the ObjectPool hit rate metric.</summary>
    /// <param name="hitRate">Pool hit rate between 0.0 and 1.0.</param>
    public void RecordPoolHitRate(double hitRate)
    {
        PoolHitRate = hitRate;
    }

    /// <summary>
    /// Capture an observational snapshot of the current metric values for export or reporting.
    /// </summary>
    /// <remarks>
    /// Values are read independently and are not transactional across concurrent updates.
    /// </remarks>
    public SmartPipeMetricsSnapshot CaptureSnapshot() =>
        new(
            Interlocked.Read(ref ItemsProcessed),
            Interlocked.Read(ref ItemsFailed),
            Interlocked.Read(ref DuplicatesFiltered),
            Interlocked.Read(ref Retries),
            Volatile.Read(ref AvgLatencyMs),
            Volatile.Read(ref SmoothLatencyMs),
            Volatile.Read(ref SmoothThroughput),
            Volatile.Read(ref QueueSize),
            Volatile.Read(ref PoolHitRate));

    /// <summary>Export all metrics as a dictionary.</summary>
    public Dictionary<string, object> Export() => CaptureSnapshot().Export();

    /// <summary>Export as JSON string.</summary>
    public string ExportJson()
    {
        var snapshot = CaptureSnapshot();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("items_processed", snapshot.ItemsProcessed);
            writer.WriteNumber("items_failed", snapshot.ItemsFailed);
            writer.WriteNumber("duplicates_filtered", snapshot.DuplicatesFiltered);
            writer.WriteNumber("retries", snapshot.Retries);
            writer.WriteNumber("avg_latency_ms", snapshot.AvgLatencyMs);
            writer.WriteNumber("smooth_latency_ms", snapshot.SmoothLatencyMs);
            writer.WriteNumber("smooth_throughput", snapshot.SmoothThroughput);
            writer.WriteNumber("queue_size", snapshot.QueueSize);
            writer.WriteNumber("pool_hit_rate", snapshot.PoolHitRate);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Export in Prometheus text format.</summary>
    public string ExportPrometheus()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in Export())
            sb.AppendLine($"smartpipe_{key} {value}");
        return sb.ToString();
    }
}
