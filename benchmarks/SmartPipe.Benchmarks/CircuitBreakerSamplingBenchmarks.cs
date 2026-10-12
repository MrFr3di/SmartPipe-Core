using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("RuntimeIo", "CircuitSampling")]
public class CircuitBreakerSamplingBenchmarks
{
    private const int BatchSize = 4096;
    private SamplingTimeProvider _time = null!;
    private CircuitBreaker _breaker = null!;
    private ContentionWorkers _workers = null!;

    [Params(1, 8, 32)]
    public int Threads { get; set; }

    [Params(1_000, 100_000)]
    public int LiveSamples { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        PrepareBreaker();
        _workers = new ContentionWorkers(Threads, thread =>
        {
            for (var index = 0; index < BatchSize / Threads; index++)
            {
                _time.Advance(1);
                using var permit = _breaker.AcquirePermit();
                if (!permit.IsAllowed)
                    throw new InvalidOperationException("Sampling benchmark unexpectedly opened the breaker.");
                if ((index + thread) % 16 == 0)
                    permit.RecordFailure();
                else
                    permit.RecordSuccess();
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup() => _workers.Dispose();

    [IterationSetup(Target = nameof(ExpireIdleBurst))]
    public void PrepareIdleBurst()
    {
        PrepareBreaker();
        _time.Advance(LiveSamples + 1L);
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void SteadyExpiry() => _workers.Run();

    [Benchmark]
    public double ExpireIdleBurst()
    {
        var ratio = _breaker.GetCurrentFailureRatio();
        if (ratio != 0)
            throw new InvalidOperationException("The idle window was not expired.");
        return ratio;
    }

    private void PrepareBreaker()
    {
        _time = new SamplingTimeProvider();
        _breaker = CreateBreaker(_time, LiveSamples);
        for (var index = 0; index < LiveSamples; index++)
        {
            _time.Advance(1);
            _breaker.RecordSuccess();
        }
    }

    internal static CircuitBreaker CreateBreaker(TimeProvider time, int windowTicks) => new(
        new CircuitBreakerOptions
        {
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromTicks(windowTicks),
            MinimumThroughput = 128,
        }, time);

    internal sealed class SamplingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(long ticks) => Interlocked.Add(ref _timestamp, ticks);
    }

    private sealed class ContentionWorkers : IDisposable
    {
        private readonly AutoResetEvent[] _starts;
        private readonly Thread[] _threads;
        private readonly CountdownEvent _finished;
        private volatile bool _stopping;
        private Exception? _error;

        public ContentionWorkers(int count, Action<int> action)
        {
            _starts = Enumerable.Range(0, count).Select(_ => new AutoResetEvent(false)).ToArray();
            _finished = new CountdownEvent(count);
            _threads = Enumerable.Range(0, count).Select(index => new Thread(() =>
            {
                while (true)
                {
                    _starts[index].WaitOne();
                    if (_stopping)
                        return;
                    try { action(index); }
                    catch (Exception error) { Interlocked.CompareExchange(ref _error, error, null); }
                    finally { _finished.Signal(); }
                }
            })
            { IsBackground = true }).ToArray();
            foreach (var thread in _threads)
                thread.Start();
        }

        public void Run()
        {
            _finished.Reset(_threads.Length);
            foreach (var start in _starts)
                start.Set();
            _finished.Wait();
            if (_error is not null)
                throw new InvalidOperationException("Contention worker failed.", _error);
        }

        public void Dispose()
        {
            _stopping = true;
            foreach (var start in _starts)
                start.Set();
            foreach (var thread in _threads)
                thread.Join();
            foreach (var start in _starts)
                start.Dispose();
            _finished.Dispose();
        }
    }
}

internal static class CircuitBreakerSamplingDiagnostics
{
    public static async Task WriteAsync(string path)
    {
        // Warm constructor/JIT paths before using public managed-heap measurements.
        var warmTime = new CircuitBreakerSamplingBenchmarks.SamplingTimeProvider();
        var warm = CircuitBreakerSamplingBenchmarks.CreateBreaker(warmTime, 1000);
        warm.RecordSuccess();
        _ = warm.GetCurrentFailureRatio();
        var rows = new List<object>();
        foreach (var samples in new[] { 1_000, 100_000, 1_000_000 })
            rows.Add(MeasureMemory(samples));
        GC.KeepAlive(warm);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows,
            new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "S1215",
        Justification = "This standalone retained-heap diagnostic requires a full collection with the breaker kept alive. It never runs in a BenchmarkDotNet timed operation or production runtime.")]
    private static long ReadRetainedHeap() => GC.GetTotalMemory(forceFullCollection: true);

    private static object MeasureMemory(int samples)
    {
        var baselineBytes = ReadRetainedHeap();
        var time = new CircuitBreakerSamplingBenchmarks.SamplingTimeProvider();
        var breaker = CircuitBreakerSamplingBenchmarks.CreateBreaker(time, samples);
        for (var index = 0; index < samples; index++)
        {
            time.Advance(1);
            breaker.RecordSuccess();
        }
        var retainedAfterFillBytes = ReadRetainedHeap() - baselineBytes;
        time.Advance(samples + (samples + 63L) / 64 + 1L);
        var started = Stopwatch.GetTimestamp();
        var ratio = breaker.GetCurrentFailureRatio();
        var expiryMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        var retainedAfterExpiryBytes = ReadRetainedHeap() - baselineBytes;
        if (ratio != 0) throw new InvalidOperationException("Diagnostic idle expiry failed.");
        GC.KeepAlive(breaker);
        return new
        {
            samples,
            bucketed = false,
            retainedAfterFillBytes,
            retainedAfterExpiryBytes,
            expiryMicroseconds,
            notes = "Managed-heap delta including clock, breaker, window, array/object headers; forced collection with breaker kept alive. Separate prototype/resource tests prove the backing-array slot bound.",
        };
    }
}
