using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SmartPipe.Core;

/// <summary>OpenTelemetry-compatible pipeline metrics using System.Diagnostics.Metrics.
/// Works with any OTLP exporter (Prometheus, Azure Monitor, Jaeger) without extra dependencies.</summary>
public class SmartPipeMetrics
{
    private static readonly Meter Meter = new("SmartPipe.Core", "1.0.0");
    
    // Counter metrics
    private readonly Counter<long> _itemsProcessedCounter;
    private readonly Counter<long> _itemsFailedCounter;
    private readonly Counter<long> _duplicatesFilteredCounter;
    private readonly Counter<long> _retriesCounter;
    
    // Histogram metrics
    private readonly Histogram<double> _latencyHistogram;
    private readonly Histogram<int> _batchSizeHistogram;
    
    // Observable metrics
    private readonly ObservableGauge<int> _queueSizeGauge;
    private readonly ObservableGauge<double> _throughputGauge;
    
    // Public properties for non-OTel consumers
    public long ItemsProcessed;
    public long ItemsFailed;
    public long DuplicatesFiltered;
    public long Retries;
    public double AvgLatencyMs;
    public double SmoothLatencyMs;
    public double SmoothThroughput;
    public int QueueSize;

    public SmartPipeMetrics()
    {
        _itemsProcessedCounter = Meter.CreateCounter<long>(
            "smartpipe.items.processed", 
            "items", 
            "Total items processed");
        
        _itemsFailedCounter = Meter.CreateCounter<long>(
            "smartpipe.items.failed", 
            "items", 
            "Total items failed");
        
        _duplicatesFilteredCounter = Meter.CreateCounter<long>(
            "smartpipe.duplicates.filtered", 
            "items", 
            "Duplicates filtered out");
        
        _retriesCounter = Meter.CreateCounter<long>(
            "smartpipe.retries", 
            "retries", 
            "Total retry attempts");
        
        _latencyHistogram = Meter.CreateHistogram<double>(
            "smartpipe.latency", 
            "ms", 
            "Processing latency in milliseconds");
        
        _batchSizeHistogram = Meter.CreateHistogram<int>(
            "smartpipe.batch.size", 
            "items", 
            "Batch size distribution");
        
        _queueSizeGauge = Meter.CreateObservableGauge(
            "smartpipe.queue.size",
            () => new Measurement<int>(QueueSize, new TagList()),
            "items",
            "Current queue size");
        
        _throughputGauge = Meter.CreateObservableGauge(
            "smartpipe.throughput",
            () => new Measurement<double>(SmoothThroughput, new TagList()),
            "items/s",
            "Smoothed throughput");
    }

    /// <summary>Record a processed item.</summary>
    public void RecordProcessed(double latencyMs)
    {
        Interlocked.Increment(ref ItemsProcessed);
        _itemsProcessedCounter.Add(1);
        _latencyHistogram.Record(latencyMs);
        
        // Update average latency
        double total = ItemsProcessed + ItemsFailed;
        AvgLatencyMs = ((AvgLatencyMs * (total - 1)) + latencyMs) / Math.Max(1, total);
    }

    /// <summary>Record a failed item.</summary>
    public void RecordFailed()
    {
        Interlocked.Increment(ref ItemsFailed);
        _itemsFailedCounter.Add(1);
    }

    /// <summary>Record a duplicate item.</summary>
    public void RecordDuplicate()
    {
        Interlocked.Increment(ref DuplicatesFiltered);
        _duplicatesFilteredCounter.Add(1);
    }

    /// <summary>Record a retry attempt.</summary>
    public void RecordRetry()
    {
        Interlocked.Increment(ref Retries);
        _retriesCounter.Add(1);
    }
}
