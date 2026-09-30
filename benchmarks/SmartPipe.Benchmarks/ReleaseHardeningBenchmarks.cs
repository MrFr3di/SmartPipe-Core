#nullable enable

using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;
using BenchmarkDotNet.Attributes;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Benchmarks;

/// <summary>Lineage append: previous copy-per-stage path versus <see cref="LineageTrail"/>.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("ReleaseHardening", "Lineage")]
public class ReleaseHardeningLineageBenchmarks
{
    private LineageEntry[] _entries = [];

    [Params(4, 16, 64)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void Setup() =>
        _entries = Enumerable.Range(0, Depth)
            .Select(index => new LineageEntry(
                $"stage-{index}",
                $"stage-{index}",
                "System.Int32",
                "System.Int32",
                DateTimeOffset.UnixEpoch,
                null,
                StageOutcome.Succeeded))
            .ToArray();

    [Benchmark(Baseline = true)]
    public int CopyPerStage()
    {
        IReadOnlyList<LineageEntry> current = [];
        foreach (var entry in _entries)
            current = current.Count == 0 ? [entry] : current.Concat([entry]).ToArray();

        return current.Count;
    }

    [Benchmark]
    public int LineageTrailAppend()
    {
        IReadOnlyList<LineageEntry> current = [];
        foreach (var entry in _entries)
            current = LineageTrail.Append(current, entry);

        return current.Count;
    }
}

/// <summary>Strict CSV record encoding: string plus exact array versus pooled chunk encoding.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("ReleaseHardening", "Csv")]
public class ReleaseHardeningCsvEncodingBenchmarks
{
    private const int RecordsPerOperation = 64;
    private readonly Encoding _encoding = new UTF8Encoding(false, true);
    private CsvBoundedRecordTextWriter _writer = null!;
    private Encoder _encoder = null!;
    private byte[]? _buffer;
    private string _record = string.Empty;

    [Params("ascii-40", "cyrillic-400", "emoji-8000")]
    public string Shape { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _record = Shape switch
        {
            "ascii-40" => "Alice,41,alice@example.com,active,2026\r\n",
            "cyrillic-400" => string.Concat(Enumerable.Repeat("Дмитрий,Москва,", 25)) + "\r\n",
            "emoji-8000" => string.Concat(Enumerable.Repeat("ab\U0001F600", 2000)) + "\r\n",
            _ => throw new InvalidOperationException(Shape),
        };
        _writer = new CsvBoundedRecordTextWriter(1 << 20, _encoding, "\r\n");
        _encoder = _encoding.GetEncoder();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_buffer is not null)
            ArrayPool<byte>.Shared.Return(_buffer);
        _writer.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = RecordsPerOperation)]
    public int StringThenExactArray()
    {
        var total = 0;
        for (var index = 0; index < RecordsPerOperation; index++)
        {
            _writer.Write(_record);
            var text = _writer.ToStringAndReset();
            var bytes = new byte[_encoding.GetByteCount(text)];
            total += _encoding.GetBytes(text.AsSpan(), bytes);
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = RecordsPerOperation)]
    public int PooledChunkEncoding()
    {
        var total = 0;
        for (var index = 0; index < RecordsPerOperation; index++)
        {
            _writer.Write(_record);
            total += _writer.EncodeAndReset(_encoder, ref _buffer);
        }

        return total;
    }
}

/// <summary>
/// Closed-state circuit breaker cost per item with a steady-state sliding window, previous versus
/// current window accounting. Each operation is one failed and one successful item (permit plus
/// record), one clock tick apart, so the window holds exactly <see cref="WindowSamples"/> samples
/// and a stable 50% failure ratio below the 1.0 threshold.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("ReleaseHardening", "CircuitBreaker")]
public class ReleaseHardeningCircuitBreakerBenchmarks
{
    private const int ContentionThreads = 4;
    private const int ContentionItemsPerThread = 256;

    private SteppingTimeProvider _time = null!;
    private CircuitBreaker _current = null!;
    private LegacyClosedStateCircuitBreaker _legacy = null!;

    [Params(1_000, 100_000)]
    public int WindowSamples { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _time = new SteppingTimeProvider();
        var samplingDuration = TimeSpan.FromTicks(WindowSamples);
        _current = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                FailureRatio = 1.0,
                SamplingDuration = samplingDuration,
                MinimumThroughput = 1,
            },
            _time);
        _legacy = new LegacyClosedStateCircuitBreaker(failureRatio: 1.0, samplingDuration, minimumThroughput: 1, _time);
        for (var index = 0; index < WindowSamples / 2; index++)
        {
            CurrentItemPair();
            LegacyItemPair();
        }

        if (_current.State != CircuitState.Closed)
            throw new InvalidOperationException("The current breaker must stay closed during the benchmark.");
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SingleThread")]
    public void Legacy_ItemPair() => LegacyItemPair();

    [Benchmark]
    [BenchmarkCategory("SingleThread")]
    public void Current_ItemPair() => CurrentItemPair();

    [Benchmark(OperationsPerInvoke = ContentionThreads * ContentionItemsPerThread)]
    [BenchmarkCategory("Contention")]
    public void Legacy_ItemPair_4Threads() => RunContended(LegacyItemPair);

    [Benchmark(OperationsPerInvoke = ContentionThreads * ContentionItemsPerThread)]
    [BenchmarkCategory("Contention")]
    public void Current_ItemPair_4Threads() => RunContended(CurrentItemPair);

    private void CurrentItemPair()
    {
        _time.Advance();
        using (var failed = _current.AcquirePermit())
            failed.RecordFailure();

        _time.Advance();
        using var succeeded = _current.AcquirePermit();
        succeeded.RecordSuccess();
    }

    private void LegacyItemPair()
    {
        _time.Advance();
        _legacy.AcquirePermit();
        _legacy.RecordFailure();
        _time.Advance();
        _legacy.AcquirePermit();
        _legacy.RecordSuccess();
    }

    private static void RunContended(Action itemPair)
    {
        using var start = new Barrier(ContentionThreads);
        var threads = new Thread[ContentionThreads];
        for (var thread = 0; thread < threads.Length; thread++)
        {
            threads[thread] = new Thread(() =>
            {
                start.SignalAndWait();
                for (var item = 0; item < ContentionItemsPerThread; item++)
                    itemPair();
            });
            threads[thread].Start();
        }

        foreach (var thread in threads)
            thread.Join();
    }
}

/// <summary>Monotonic time source advanced explicitly, one tick per recorded item.</summary>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public void Advance() => Interlocked.Increment(ref _timestamp);
}

/// <summary>
/// Closed-state path of the circuit breaker before the release hardening changes, kept only as a
/// benchmark baseline: <c>AcquirePermit</c> pruned the window under the lock, samples were stamped
/// outside the lock, and every failure counted the window by scanning it under the lock.
/// Half-open handling is omitted because the benchmark never leaves the closed state.
/// </summary>
internal sealed class LegacyClosedStateCircuitBreaker(
    double failureRatio,
    TimeSpan samplingDuration,
    int minimumThroughput,
    TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(long Timestamp, bool IsSuccess)> _window = new();
    private readonly object _windowGate = new();
    private double _ewmaFailureRate;
    private int _opened;

    public bool IsOpen => Volatile.Read(ref _opened) != 0;

    public bool AcquirePermit()
    {
        CleanupWindow();
        return !IsOpen;
    }

    public void RecordSuccess()
    {
        EnqueueWindowSample(isSuccess: true);
        CleanupWindow();
        double alpha = _ewmaFailureRate > 0.1 ? 0.5 : 0.2;
        AtomicHelper.CompareExchangeLoop(ref _ewmaFailureRate, current => (1.0 - alpha) * current);
    }

    public void RecordFailure()
    {
        EnqueueWindowSample(isSuccess: false);
        CleanupWindow();
        double alpha = _ewmaFailureRate > 0.1 ? 0.5 : 0.2;
        AtomicHelper.CompareExchangeLoop(ref _ewmaFailureRate, current => alpha * 1.0 + (1.0 - alpha) * current);
        if (_ewmaFailureRate > failureRatio * 1.5)
            EnqueueWindowSample(isSuccess: false);

        var (total, failures) = GetWindowStatistics();
        if (total >= minimumThroughput && (double)failures / total >= failureRatio)
            Volatile.Write(ref _opened, 1);
    }

    private void CleanupWindow()
    {
        var now = time.GetTimestamp();
        while (true)
        {
            (long Timestamp, bool IsSuccess) candidate;
            lock (_windowGate)
            {
                if (!_window.TryPeek(out candidate))
                    return;
            }

            if (time.GetElapsedTime(candidate.Timestamp, now) <= samplingDuration)
                return;

            lock (_windowGate)
            {
                if (!_window.TryPeek(out var current) || current != candidate)
                    continue;

                _ = _window.TryDequeue(out _);
            }
        }
    }

    private void EnqueueWindowSample(bool isSuccess)
    {
        var timestamp = time.GetTimestamp();
        lock (_windowGate)
            _window.Enqueue((timestamp, isSuccess));
    }

    private (int Total, int Failures) GetWindowStatistics()
    {
        lock (_windowGate)
        {
            var total = 0;
            var failures = 0;
            foreach (var (_, isSuccess) in _window)
            {
                total++;
                if (!isSuccess)
                    failures++;
            }

            return (total, failures);
        }
    }
}

/// <summary>Per-measurement cost of the pipeline tag with and without an enabled listener.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("ReleaseHardening", "Metrics")]
public class ReleaseHardeningMetricsBenchmarks
{
    private SmartPipeMetricsRecorder _untagged = null!;
    private SmartPipeMetricsRecorder _tagged = null!;
    private MeterListener? _listener;

    [Params(false, true)]
    public bool ListenerEnabled { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _untagged = new SmartPipeMetricsRecorder(SystemPipelineClock.Instance, pipelineId: null);
        _tagged = new SmartPipeMetricsRecorder(SystemPipelineClock.Instance, pipelineId: "orders");
        if (!ListenerEnabled)
            return;

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SmartPipeMeter.Name)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        _listener.Start();
    }

    [GlobalCleanup]
    public void Cleanup() => _listener?.Dispose();

    [Benchmark(Baseline = true)]
    public void RecordProcessed_Untagged() => _untagged.RecordProcessed(1.5);

    [Benchmark]
    public void RecordProcessed_PipelineTag() => _tagged.RecordProcessed(1.5);
}

/// <summary>
/// Pipeline-tag cost with the OpenTelemetry SDK aggregating: recording across 1, 10 or 100 stable
/// pipeline ids, and one collect/export cycle through the in-memory exporter.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("ReleaseHardening", "OpenTelemetry")]
public class ReleaseHardeningOpenTelemetryBenchmarks
{
    private readonly List<Metric> _exported = [];
    private MeterProvider _provider = null!;
    private SmartPipeMetricsRecorder[] _recorders = [];
    private SmartPipeMetricsRecorder _untagged = null!;
    private int _next;

    [Params(1, 10, 100)]
    public int PipelineCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SmartPipeMeter.Name)
            .AddInMemoryExporter(_exported)
            .Build()!;
        _untagged = new SmartPipeMetricsRecorder(SystemPipelineClock.Instance, pipelineId: null);
        _recorders = Enumerable.Range(0, PipelineCount)
            .Select(index => new SmartPipeMetricsRecorder(SystemPipelineClock.Instance, $"pipeline-{index:D3}"))
            .ToArray();

        // Cardinality check: one processed point per stable pipeline id (plus the untagged series).
        foreach (var recorder in _recorders)
            recorder.RecordProcessed(1.5);
        _untagged.RecordProcessed(1.5);
        _provider.ForceFlush();
        var points = 0;
        foreach (var metric in _exported.Where(metric => metric.Name == "smartpipe.items.processed"))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                _ = point;
                points++;
            }
        }

        if (points != PipelineCount + 1)
            throw new InvalidOperationException($"Expected {PipelineCount + 1} processed series, found {points}.");
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark(Baseline = true)]
    public void RecordProcessed_Untagged_Sdk() => _untagged.RecordProcessed(1.5);

    [Benchmark]
    public void RecordProcessed_PipelineTags_Sdk()
    {
        var index = _next;
        _next = index + 1 == _recorders.Length ? 0 : index + 1;
        _recorders[index].RecordProcessed(1.5);
    }

    [Benchmark]
    public int CollectAndExport()
    {
        _exported.Clear();
        _provider.ForceFlush();
        return _exported.Count;
    }
}
