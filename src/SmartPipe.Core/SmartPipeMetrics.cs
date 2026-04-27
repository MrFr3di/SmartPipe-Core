using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SmartPipe.Core;

/// <summary>OpenTelemetry-compatible pipeline metrics. All instruments are static singletons.</summary>
public class SmartPipeMetrics
{
    private static readonly Meter Meter = new("SmartPipe.Core", "1.0.1");
    
    private static readonly Counter<long> ItemsProcessedCounter = 
        Meter.CreateCounter<long>("smartpipe.items.processed", "items", "Total items processed");
    private static readonly Counter<long> ItemsFailedCounter = 
        Meter.CreateCounter<long>("smartpipe.items.failed", "items", "Total items failed");
    private static readonly Counter<long> DuplicatesFilteredCounter = 
        Meter.CreateCounter<long>("smartpipe.duplicates.filtered", "items", "Duplicates filtered out");
    private static readonly Counter<long> RetriesCounter = 
        Meter.CreateCounter<long>("smartpipe.retries", "retries", "Total retry attempts");
    private static readonly Histogram<double> LatencyHistogram = 
        Meter.CreateHistogram<double>("smartpipe.latency", "ms", "Processing latency");
    
    // Mutable fields for current values
    public long ItemsProcessed;
    public long ItemsFailed;
    public long DuplicatesFiltered;
    public long Retries;
    public double AvgLatencyMs;
    public double SmoothLatencyMs;
    public double SmoothThroughput;
    public int QueueSize;
    public double PoolHitRate;

    /// <summary>Record a processed item.</summary>
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
}
