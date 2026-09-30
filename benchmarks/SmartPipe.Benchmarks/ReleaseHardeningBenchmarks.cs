#nullable enable

using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;
using BenchmarkDotNet.Attributes;
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

/// <summary>Circuit breaker failure recording cost as the sliding window grows.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("ReleaseHardening", "CircuitBreaker")]
public class ReleaseHardeningCircuitBreakerBenchmarks
{
    private CircuitBreaker _breaker = null!;

    [Params(1_000, 100_000)]
    public int WindowSamples { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Failure ratio 1.0 keeps the breaker closed while successes dominate the window.
        _breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromHours(1),
                MinimumThroughput = 1,
            },
            TimeProvider.System);
        for (var index = 0; index < WindowSamples; index++)
            _breaker.RecordSuccess();
    }

    [Benchmark]
    public void RecordFailureThenSuccess()
    {
        _breaker.RecordFailure();
        _breaker.RecordSuccess();
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
