#nullable enable

using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;

namespace SmartPipe.Core;

/// <summary>Meter instruments published by SmartPipe runtime metrics.</summary>
public static class SmartPipeMeter
{
    /// <summary>Canonical meter name for SmartPipe.Core.</summary>
    public const string Name = SmartPipeDiagnostics.MeterName;

    internal static readonly Meter Meter = new(
        Name,
        typeof(SmartPipeMeter).Assembly.GetName().Version?.ToString() ?? "1.0.0"
    );

    internal static readonly Counter<long> ItemsProcessedCounter = Meter.CreateCounter<long>(
        "smartpipe.items.processed",
        "items"
    );

    internal static readonly Counter<long> ItemsFailedCounter = Meter.CreateCounter<long>(
        "smartpipe.items.failed",
        "items"
    );

    internal static readonly Counter<long> ItemsFilteredCounter = Meter.CreateCounter<long>(
        "smartpipe.items.filtered",
        "items"
    );

    internal static readonly Counter<long> ItemsDroppedCounter = Meter.CreateCounter<long>(
        "smartpipe.items.dropped",
        "items"
    );

    internal static readonly Counter<long> OutputItemsDroppedCounter = Meter.CreateCounter<long>(
        "smartpipe.output.items.dropped",
        "items"
    );

    internal static readonly Counter<long> ObserverEventsDroppedCounter = Meter.CreateCounter<long>(
        "smartpipe.observer.events.dropped",
        "events"
    );

    internal static readonly Counter<long> ItemsRetriedCounter = Meter.CreateCounter<long>(
        "smartpipe.items.retried",
        "items"
    );

    internal static readonly Counter<long> ItemsDeadLetteredCounter = Meter.CreateCounter<long>(
        "smartpipe.items.deadlettered",
        "items"
    );

    internal static readonly Counter<long> DuplicatesFilteredCounter = Meter.CreateCounter<long>(
        "smartpipe.items.duplicates_filtered",
        "items"
    );

    internal static readonly Histogram<double> StageLatencyHistogram = Meter.CreateHistogram<double>(
        "smartpipe.stage.duration",
        "ms"
    );

    internal static readonly Histogram<double> SinkLatencyHistogram = Meter.CreateHistogram<double>(
        "smartpipe.sink.duration",
        "ms"
    );
}

/// <summary>Immutable point-in-time sample of SmartPipe metric values.</summary>
public sealed record SmartPipeMetricsSnapshot
{
    /// <summary>Gets an empty immutable metrics snapshot.</summary>
    public static SmartPipeMetricsSnapshot Empty { get; } = new(
        itemsProcessed: 0,
        itemsFailed: 0,
        itemsFiltered: 0,
        itemsDropped: 0,
        outputItemsDropped: 0,
        observerEventsDropped: 0,
        itemsRetried: 0,
        itemsDeadLettered: 0,
        inputQueueDepth: 0,
        outputQueueDepth: 0,
        lastStageLatencyMs: 0,
        lastProcessedAtUtc: null,
        duplicatesFiltered: 0,
        avgLatencyMs: 0,
        smoothLatencyMs: 0,
        smoothThroughput: 0,
        queueSize: 0,
        poolHitRate: 0);

    /// <summary>Create an immutable point-in-time sample of SmartPipe metric values.</summary>
    public SmartPipeMetricsSnapshot(
        long itemsProcessed,
        long itemsFailed,
        long itemsFiltered,
        long itemsDropped,
        long outputItemsDropped,
        long observerEventsDropped,
        long itemsRetried,
        long itemsDeadLettered,
        int inputQueueDepth,
        int outputQueueDepth,
        double lastStageLatencyMs,
        DateTimeOffset? lastProcessedAtUtc,
        long duplicatesFiltered,
        double avgLatencyMs,
        double smoothLatencyMs,
        double smoothThroughput,
        int queueSize,
        double poolHitRate)
        : this(
            itemsProcessed,
            itemsFailed,
            itemsFiltered,
            itemsDropped,
            outputItemsDropped,
            observerEventsDropped,
            itemsRetried,
            itemsDeadLettered,
            inputQueueDepth,
            outputQueueDepth,
            lastStageLatencyMs,
            lastProcessedAtUtc,
            lastActivityAtUtc: lastProcessedAtUtc,
            duplicatesFiltered,
            avgLatencyMs,
            smoothLatencyMs,
            smoothThroughput,
            queueSize,
            poolHitRate)
    {
    }

    /// <summary>Create an immutable point-in-time sample of SmartPipe metric values.</summary>
    public SmartPipeMetricsSnapshot(
        long itemsProcessed,
        long itemsFailed,
        long itemsFiltered,
        long itemsDropped,
        long outputItemsDropped,
        long observerEventsDropped,
        long itemsRetried,
        long itemsDeadLettered,
        int inputQueueDepth,
        int outputQueueDepth,
        double lastStageLatencyMs,
        DateTimeOffset? lastProcessedAtUtc,
        DateTimeOffset? lastActivityAtUtc,
        long duplicatesFiltered,
        double avgLatencyMs,
        double smoothLatencyMs,
        double smoothThroughput,
        int queueSize,
        double poolHitRate)
    {
        ItemsProcessed = itemsProcessed;
        ItemsFailed = itemsFailed;
        ItemsFiltered = itemsFiltered;
        ItemsDropped = itemsDropped;
        OutputItemsDropped = outputItemsDropped;
        ObserverEventsDropped = observerEventsDropped;
        ItemsRetried = itemsRetried;
        ItemsDeadLettered = itemsDeadLettered;
        InputQueueDepth = inputQueueDepth;
        OutputQueueDepth = outputQueueDepth;
        LastStageLatencyMs = lastStageLatencyMs;
        LastProcessedAtUtc = lastProcessedAtUtc;
        LastActivityAtUtc = lastActivityAtUtc;
        DuplicatesFiltered = duplicatesFiltered;
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

    /// <summary>Total items filtered as normal terminal control flow in the sampled view.</summary>
    public long ItemsFiltered { get; }

    /// <summary>Total input items dropped by bounded channel policy in the sampled view.</summary>
    public long ItemsDropped { get; }

    /// <summary>Total output items dropped by bounded channel policy in the sampled view.</summary>
    public long OutputItemsDropped { get; }

    /// <summary>Total observer events dropped by buffered dispatch pressure in the sampled view.</summary>
    public long ObserverEventsDropped { get; }

    /// <summary>Total retry attempts made in the sampled view.</summary>
    public long ItemsRetried { get; }

    /// <summary>Total items written to dead-letter handling in the sampled view.</summary>
    public long ItemsDeadLettered { get; }

    /// <summary>Current input queue depth in the sampled view.</summary>
    public int InputQueueDepth { get; }

    /// <summary>Current output queue depth in the sampled view.</summary>
    public int OutputQueueDepth { get; }

    /// <summary>Most recent stage latency in milliseconds in the sampled view.</summary>
    public double LastStageLatencyMs { get; }

    /// <summary>Last successful processed timestamp in the sampled view.</summary>
    public DateTimeOffset? LastProcessedAtUtc { get; }

    /// <summary>Most recent accepted or terminal work activity timestamp in the sampled view.</summary>
    public DateTimeOffset? LastActivityAtUtc { get; }

    /// <summary>Total duplicate items filtered out in the sampled view.</summary>
    public long DuplicatesFiltered { get; }

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

    /// <summary>Compatibility name for total retry attempts in the sampled view.</summary>
    public long Retries => ItemsRetried;

    /// <summary>Export the sampled values as a dictionary.</summary>
    public Dictionary<string, object> Export() =>
        new()
        {
            ["items_processed"] = ItemsProcessed,
            ["items_failed"] = ItemsFailed,
            ["items_filtered"] = ItemsFiltered,
            ["items_dropped"] = ItemsDropped,
            ["output_items_dropped"] = OutputItemsDropped,
            ["observer_events_dropped"] = ObserverEventsDropped,
            ["duplicates_filtered"] = DuplicatesFiltered,
            ["retries"] = Retries,
            ["items_dead_lettered"] = ItemsDeadLettered,
            ["avg_latency_ms"] = AvgLatencyMs,
            ["last_stage_latency_ms"] = LastStageLatencyMs,
            ["smooth_latency_ms"] = SmoothLatencyMs,
            ["smooth_throughput"] = SmoothThroughput,
            ["queue_size"] = QueueSize,
            ["input_queue_depth"] = InputQueueDepth,
            ["output_queue_depth"] = OutputQueueDepth,
            ["pool_hit_rate"] = PoolHitRate,
            ["last_processed_at_utc"] = LastProcessedAtUtc?.ToString("O") ?? string.Empty,
            ["last_activity_at_utc"] = LastActivityAtUtc?.ToString("O") ?? string.Empty,
        };
}

/// <summary>Thread-safe mutable recorder that owns SmartPipe metric state.</summary>
public sealed class SmartPipeMetricsRecorder
{
    private readonly IPipelineClock _clock;
    private long _itemsProcessed;
    private long _itemsFailed;
    private long _itemsFiltered;
    private long _itemsDropped;
    private long _outputItemsDropped;
    private long _observerEventsDropped;
    private long _itemsRetried;
    private long _itemsDeadLettered;
    private long _duplicatesFiltered;
    private int _inputQueueDepth;
    private int _outputQueueDepth;
    private long _lastProcessedAtUtcTicks;
    private long _lastActivityAtUtcTicks;
    private double _totalLatencyMs;
    private double _lastStageLatencyMs;
    private double _smoothLatencyMs;
    private double _smoothThroughput;
    private double _poolHitRate;

    /// <summary>Creates a metrics recorder backed by the system runtime clock.</summary>
    public SmartPipeMetricsRecorder()
        : this(SystemPipelineClock.Instance)
    {
    }

    internal SmartPipeMetricsRecorder(IPipelineClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Total items successfully processed.</summary>
    public long ItemsProcessed => Interlocked.Read(ref _itemsProcessed);

    /// <summary>Total items that failed processing.</summary>
    public long ItemsFailed => Interlocked.Read(ref _itemsFailed);

    /// <summary>Total items filtered as normal terminal control flow.</summary>
    public long ItemsFiltered => Interlocked.Read(ref _itemsFiltered);

    /// <summary>Total input items dropped by bounded channel policy.</summary>
    public long ItemsDropped => Interlocked.Read(ref _itemsDropped);

    /// <summary>Total output items dropped by bounded channel policy.</summary>
    public long OutputItemsDropped => Interlocked.Read(ref _outputItemsDropped);

    /// <summary>Total observer events dropped by buffered dispatch pressure.</summary>
    public long ObserverEventsDropped => Interlocked.Read(ref _observerEventsDropped);

    /// <summary>Total retry attempts made.</summary>
    public long ItemsRetried => Interlocked.Read(ref _itemsRetried);

    /// <summary>Total items written to dead-letter handling.</summary>
    public long ItemsDeadLettered => Interlocked.Read(ref _itemsDeadLettered);

    /// <summary>Total duplicate items filtered out.</summary>
    public long DuplicatesFiltered => Interlocked.Read(ref _duplicatesFiltered);

    /// <summary>Current input queue depth.</summary>
    public int InputQueueDepth => Volatile.Read(ref _inputQueueDepth);

    /// <summary>Current output queue depth.</summary>
    public int OutputQueueDepth => Volatile.Read(ref _outputQueueDepth);

    /// <summary>Most recent stage latency in milliseconds.</summary>
    public double LastStageLatencyMs => Volatile.Read(ref _lastStageLatencyMs);

    /// <summary>Running average stage latency in milliseconds.</summary>
    public double AvgLatencyMs
    {
        get
        {
            var processed = ItemsProcessed;
            if (processed == 0)
                return 0;

            return Volatile.Read(ref _totalLatencyMs) / processed;
        }
    }

    /// <summary>EMA-smoothed latency in milliseconds.</summary>
    public double SmoothLatencyMs => Volatile.Read(ref _smoothLatencyMs);

    /// <summary>EMA-smoothed throughput in items per second.</summary>
    public double SmoothThroughput => Volatile.Read(ref _smoothThroughput);

    /// <summary>Current queue size compatibility value.</summary>
    public int QueueSize => InputQueueDepth;

    /// <summary>ObjectPool hit rate in the range 0.0-1.0.</summary>
    public double PoolHitRate => Volatile.Read(ref _poolHitRate);

    /// <summary>Last successful processed timestamp.</summary>
    public DateTimeOffset? LastProcessedAtUtc
    {
        get
        {
            return ReadTimestamp(ref _lastProcessedAtUtcTicks);
        }
    }

    /// <summary>Most recent accepted or terminal work activity timestamp.</summary>
    public DateTimeOffset? LastActivityAtUtc
    {
        get
        {
            return ReadTimestamp(ref _lastActivityAtUtcTicks);
        }
    }

    /// <summary>Record a processed item and its stage latency.</summary>
    public void RecordProcessed(double latencyMs)
    {
        var now = _clock.GetUtcNow();
        Interlocked.Increment(ref _itemsProcessed);
        AddDouble(ref _totalLatencyMs, latencyMs);
        Volatile.Write(ref _lastStageLatencyMs, latencyMs);
        MaxTimestamp(ref _lastProcessedAtUtcTicks, now.UtcTicks);
        RecordActivity(now);
        SmartPipeMeter.ItemsProcessedCounter.Add(1);
        SmartPipeMeter.StageLatencyHistogram.Record(latencyMs);
    }

    /// <summary>Record a failed item.</summary>
    public void RecordFailed()
    {
        Interlocked.Increment(ref _itemsFailed);
        RecordActivity();
        SmartPipeMeter.ItemsFailedCounter.Add(1);
    }

    /// <summary>Record a filtered item.</summary>
    public void RecordFiltered()
    {
        Interlocked.Increment(ref _itemsFiltered);
        RecordActivity();
        SmartPipeMeter.ItemsFilteredCounter.Add(1);
    }

    /// <summary>Record an input item dropped by bounded channel policy.</summary>
    public void RecordItemDropped()
    {
        Interlocked.Increment(ref _itemsDropped);
        RecordActivity();
        SmartPipeMeter.ItemsDroppedCounter.Add(1);
    }

    /// <summary>Record an output item dropped by bounded channel policy.</summary>
    public void RecordOutputDropped()
    {
        Interlocked.Increment(ref _outputItemsDropped);
        RecordActivity();
        SmartPipeMeter.OutputItemsDroppedCounter.Add(1);
    }

    /// <summary>Record an observer event dropped by buffered dispatch pressure.</summary>
    public void RecordObserverEventDropped()
    {
        Interlocked.Increment(ref _observerEventsDropped);
        SmartPipeMeter.ObserverEventsDroppedCounter.Add(1);
    }

    /// <summary>Record a filtered duplicate.</summary>
    public void RecordDuplicate()
    {
        Interlocked.Increment(ref _duplicatesFiltered);
        RecordActivity();
        SmartPipeMeter.DuplicatesFilteredCounter.Add(1);
    }

    /// <summary>Record a retry attempt.</summary>
    public void RecordRetry()
    {
        Interlocked.Increment(ref _itemsRetried);
        RecordActivity();
        SmartPipeMeter.ItemsRetriedCounter.Add(1);
    }

    /// <summary>Record a dead-lettered item.</summary>
    public void RecordDeadLetter()
    {
        Interlocked.Increment(ref _itemsDeadLettered);
        RecordActivity();
        SmartPipeMeter.ItemsDeadLetteredCounter.Add(1);
    }

    internal void RecordActivity()
    {
        RecordActivity(_clock.GetUtcNow());
    }

    internal void RecordSinkDuration(double latencyMs)
    {
        SmartPipeMeter.SinkLatencyHistogram.Record(latencyMs);
    }

    /// <summary>Update current input and output queue depths.</summary>
    public void UpdateQueueDepths(int inputQueueDepth, int outputQueueDepth)
    {
        Volatile.Write(ref _inputQueueDepth, inputQueueDepth);
        Volatile.Write(ref _outputQueueDepth, outputQueueDepth);
    }

    /// <summary>Update the compatibility queue size value.</summary>
    public void UpdateQueueSize(int queueSize) => UpdateQueueDepths(queueSize, OutputQueueDepth);

    /// <summary>Update smoothed latency and throughput values.</summary>
    public void UpdateSmoothing(double smoothLatencyMs, double smoothThroughput)
    {
        Volatile.Write(ref _smoothLatencyMs, smoothLatencyMs);
        Volatile.Write(ref _smoothThroughput, smoothThroughput);
    }

    /// <summary>Update the ObjectPool hit rate metric.</summary>
    public void RecordPoolHitRate(double hitRate)
    {
        Volatile.Write(ref _poolHitRate, hitRate);
    }

    /// <summary>Capture an immutable observational snapshot.</summary>
    public SmartPipeMetricsSnapshot CaptureSnapshot()
    {
        var itemsProcessed = ItemsProcessed;
        var avgLatency = itemsProcessed == 0
            ? 0
            : Volatile.Read(ref _totalLatencyMs) / itemsProcessed;

        return new SmartPipeMetricsSnapshot(
            itemsProcessed,
            ItemsFailed,
            ItemsFiltered,
            ItemsDropped,
            OutputItemsDropped,
            ObserverEventsDropped,
            ItemsRetried,
            ItemsDeadLettered,
            InputQueueDepth,
            OutputQueueDepth,
            LastStageLatencyMs,
            LastProcessedAtUtc,
            LastActivityAtUtc,
            DuplicatesFiltered,
            avgLatency,
            SmoothLatencyMs,
            SmoothThroughput,
            QueueSize,
            PoolHitRate);
    }

    private static void AddDouble(ref double location, double value)
    {
        double current;
        double next;
        do
        {
            current = Volatile.Read(ref location);
            next = current + value;
        } while (Interlocked.CompareExchange(ref location, next, current) != current);
    }

    private static DateTimeOffset? ReadTimestamp(ref long location)
    {
        var ticks = Interlocked.Read(ref location);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private void RecordActivity(DateTimeOffset timestamp)
    {
        MaxTimestamp(ref _lastActivityAtUtcTicks, timestamp.UtcTicks);
    }

    private static void MaxTimestamp(ref long location, long ticks)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref location);
            if (ticks <= current)
                return;
        } while (Interlocked.CompareExchange(ref location, ticks, current) != current);
    }
}

/// <summary>Compatibility metrics facade backed by <see cref="SmartPipeMetricsRecorder"/>.</summary>
public class SmartPipeMetrics
{
    private readonly SmartPipeMetricsRecorder _recorder = new();

    /// <summary>Total items successfully processed.</summary>
    public long ItemsProcessed => _recorder.ItemsProcessed;

    /// <summary>Total items that failed processing.</summary>
    public long ItemsFailed => _recorder.ItemsFailed;

    /// <summary>Total duplicate items filtered out.</summary>
    public long DuplicatesFiltered => _recorder.DuplicatesFiltered;

    /// <summary>Total items filtered as normal terminal control flow.</summary>
    public long ItemsFiltered => _recorder.ItemsFiltered;

    /// <summary>Total input items dropped by bounded channel policy.</summary>
    public long ItemsDropped => _recorder.ItemsDropped;

    /// <summary>Total output items dropped by bounded channel policy.</summary>
    public long OutputItemsDropped => _recorder.OutputItemsDropped;

    /// <summary>Total observer events dropped by buffered dispatch pressure.</summary>
    public long ObserverEventsDropped => _recorder.ObserverEventsDropped;

    /// <summary>Total retry attempts made.</summary>
    public long Retries => _recorder.ItemsRetried;

    /// <summary>Running average latency in milliseconds.</summary>
    public double AvgLatencyMs => _recorder.AvgLatencyMs;

    /// <summary>Most recent stage latency in milliseconds.</summary>
    public double LastStageLatencyMs => _recorder.LastStageLatencyMs;

    /// <summary>EMA-smoothed latency in milliseconds.</summary>
    public double SmoothLatencyMs => _recorder.SmoothLatencyMs;

    /// <summary>EMA-smoothed throughput in items per second.</summary>
    public double SmoothThroughput => _recorder.SmoothThroughput;

    /// <summary>Current queue size.</summary>
    public int QueueSize => _recorder.QueueSize;

    /// <summary>ObjectPool hit rate in the range 0.0-1.0.</summary>
    public double PoolHitRate => _recorder.PoolHitRate;

    /// <summary>Record a processed item and its latency.</summary>
    public void RecordProcessed(double latencyMs) => _recorder.RecordProcessed(latencyMs);

    /// <summary>Record a failed item.</summary>
    public void RecordFailed() => _recorder.RecordFailed();

    /// <summary>Record a filtered item.</summary>
    public void RecordFiltered() => _recorder.RecordFiltered();

    /// <summary>Record an input item dropped by bounded channel policy.</summary>
    public void RecordItemDropped() => _recorder.RecordItemDropped();

    /// <summary>Record an output item dropped by bounded channel policy.</summary>
    public void RecordOutputDropped() => _recorder.RecordOutputDropped();

    /// <summary>Record an observer event dropped by buffered dispatch pressure.</summary>
    public void RecordObserverEventDropped() => _recorder.RecordObserverEventDropped();

    /// <summary>Record a filtered duplicate.</summary>
    public void RecordDuplicate() => _recorder.RecordDuplicate();

    /// <summary>Record a retry attempt.</summary>
    public void RecordRetry() => _recorder.RecordRetry();

    /// <summary>Record a dead-lettered item.</summary>
    public void RecordDeadLetter() => _recorder.RecordDeadLetter();

    /// <summary>Update input queue size.</summary>
    public void UpdateQueueSize(int queueSize) => _recorder.UpdateQueueSize(queueSize);

    /// <summary>Update input and output queue depths.</summary>
    public void UpdateQueueDepths(int inputQueueDepth, int outputQueueDepth) =>
        _recorder.UpdateQueueDepths(inputQueueDepth, outputQueueDepth);

    /// <summary>Update smoothed latency and throughput.</summary>
    public void UpdateSmoothing(double smoothLatencyMs, double smoothThroughput) =>
        _recorder.UpdateSmoothing(smoothLatencyMs, smoothThroughput);

    /// <summary>Update the ObjectPool hit rate metric.</summary>
    public void RecordPoolHitRate(double hitRate) => _recorder.RecordPoolHitRate(hitRate);

    /// <summary>Capture an immutable observational snapshot.</summary>
    public SmartPipeMetricsSnapshot CaptureSnapshot() => _recorder.CaptureSnapshot();

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
            foreach (var (key, value) in snapshot.Export())
            {
                switch (value)
                {
                    case long longValue:
                        writer.WriteNumber(key, longValue);
                        break;
                    case int intValue:
                        writer.WriteNumber(key, intValue);
                        break;
                    case double doubleValue:
                        writer.WriteNumber(key, doubleValue);
                        break;
                    case string stringValue:
                        writer.WriteString(key, stringValue);
                        break;
                    default:
                        writer.WriteString(key, value.ToString());
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Exports a diagnostic text snapshot for logs and support dumps.</summary>
    public string ToDiagnosticText()
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in Export())
            sb.AppendLine($"smartpipe_{key} {value}");
        return sb.ToString();
    }

    /// <summary>Exports a diagnostic text snapshot.</summary>
    [Obsolete("Use ToDiagnosticText(). Prometheus export should be provided by OpenTelemetry exporters.")]
    public string ExportPrometheus() => ToDiagnosticText();
}
