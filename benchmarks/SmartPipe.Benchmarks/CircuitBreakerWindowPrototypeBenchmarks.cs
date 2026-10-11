using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("CircuitSamplingPrototype")]
public class CircuitBreakerWindowPrototypeBenchmarks
{
    private const int BatchSize = 4096;
    private CircuitBreakerSamplingBenchmarks.SamplingTimeProvider _time = null!;
    private ICircuitBreakerSamplingWindow _window = null!;
    private CircuitBreakerSamplingBenchmarks.ContentionWorkers _workers = null!;
    [Params(false, true)] public bool Bucketed { get; set; }
    [Params(1, 8, 32)] public int Threads { get; set; }
    [Params(1000, 100_000)] public int LiveSamples { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Prepare();
        _workers = new CircuitBreakerSamplingBenchmarks.ContentionWorkers(Threads, thread =>
        {
            for (var i = 0; i < BatchSize / Threads; i++)
            {
                _time.Advance(1);
                _window.AddSample((i + thread) % 16 != 0);
                _window.ExpireAndGetStatistics();
            }
        });
    }
    [GlobalCleanup] public void Cleanup() => _workers.Dispose();
    [IterationSetup(Target = nameof(IdleExpiry))]
    public void PrepareIdle()
    {
        Prepare();
        _time.Advance(2L * LiveSamples + 1);
    }
    [Benchmark(OperationsPerInvoke = BatchSize)] public void SteadyExpiry() => _workers.Run();
    [Benchmark]
    public long IdleExpiry()
    {
        var total = _window.ExpireAndGetStatistics().Total;
        if (total != 0) throw new InvalidOperationException("Prototype idle window did not expire completely.");
        return total;
    }
    private void Prepare()
    {
        _time = new CircuitBreakerSamplingBenchmarks.SamplingTimeProvider();
        var source = new TimeProviderCircuitBreakerTimeSource(_time);
        _window = Bucketed
            ? new BucketedCircuitBreakerSamplingWindow(TimeSpan.FromTicks(LiveSamples), 64, source)
            : new ExactCircuitBreakerSamplingWindow(TimeSpan.FromTicks(LiveSamples), source);
        for (var i = 0; i < LiveSamples; i++) { _time.Advance(1); _window.AddSample(true); }
    }

    internal static async Task WriteMemoryDiagnosticsAsync(string path)
    {
        var rows = new List<object>();
        foreach (var samples in new[] { 1000, 100_000, 1_000_000 })
            foreach (var bucketed in new[] { false, true })
            {
                rows.Add(MeasureMemory(samples, bucketed));
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static object MeasureMemory(int samples, bool bucketed)
    {
        var time = new CircuitBreakerSamplingBenchmarks.SamplingTimeProvider();
        var source = new TimeProviderCircuitBreakerTimeSource(time);
        var duration = TimeSpan.FromSeconds(30);
        ICircuitBreakerSamplingWindow window = bucketed
            ? new BucketedCircuitBreakerSamplingWindow(duration, 64, source)
            : new ExactCircuitBreakerSamplingWindow(duration, source);
        var began = Stopwatch.GetTimestamp();
        for (var i = 0; i < samples; i++) { time.Advance(duration.Ticks / samples); window.AddSample(true); }
        var fillSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        if (window.GetStatistics().Total != samples) throw new InvalidOperationException("Prototype peak sample count mismatch.");
        var peakBytes = Storage(window);
        time.Advance(duration.Ticks * 2);
        began = Stopwatch.GetTimestamp();
        var expired = window.ExpireAndGetStatistics();
        var expireMicroseconds = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
        if (expired.Total != 0) throw new InvalidOperationException("Prototype idle sample count mismatch.");
        return new
        {
            samples,
            bucketed,
            samplingSeconds = 30,
            configuredSamplesPerSecond = samples / 30d,
            observedFillOperationsPerSecond = samples / fillSeconds,
            peakBytes,
            idleBytes = Storage(window),
            expireMicroseconds,
            bucketSlots = bucketed ? ((BucketedCircuitBreakerSamplingWindow)window).StorageSlotCount : (int?)null,
            notes = "Backing arrays only; timing diagnostics are separate from BenchmarkDotNet performance evidence."
        };
        static long Storage(ICircuitBreakerSamplingWindow window) => window switch
        {
            ExactCircuitBreakerSamplingWindow exact => exact.RetainedStorageBytes,
            BucketedCircuitBreakerSamplingWindow bucket => bucket.RetainedStorageBytes,
            _ => throw new InvalidOperationException(),
        };
    }
}
