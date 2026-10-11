using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [Params("Exact", "Bucketed")]
    public string SamplingMode { get; set; } = "Exact";

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
        _time.Advance(LiveSamples + (LiveSamples + 63L) / 64 + 1L);
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
        _breaker = CreateBreaker(_time, LiveSamples, SamplingMode);
        for (var index = 0; index < LiveSamples; index++)
        {
            _time.Advance(1);
            _breaker.RecordSuccess();
        }
    }

    internal static CircuitBreaker CreateBreaker(TimeProvider time, int windowTicks, string samplingMode = "Exact")
    {
        var options = new CircuitBreakerOptions
        {
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromTicks(windowTicks),
            MinimumThroughput = 128,
        };
        if (samplingMode != "Exact")
        {
            var property = typeof(CircuitBreakerOptions).GetProperty("SamplingMode")
                ?? throw new InvalidOperationException("Bucketed sampling is unavailable in this candidate.");
            property.SetValue(options, Enum.Parse(property.PropertyType, samplingMode));
        }
        return new CircuitBreaker(options, time);
    }

    internal sealed class SamplingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(long ticks) => Interlocked.Add(ref _timestamp, ticks);
    }

    internal sealed class ContentionWorkers : IDisposable
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
        var rows = new List<object>();
        foreach (var samples in new[] { 1_000, 100_000, 1_000_000 })
        {
            var time = new CircuitBreakerSamplingBenchmarks.SamplingTimeProvider();
            var breaker = CircuitBreakerSamplingBenchmarks.CreateBreaker(time, samples);
            for (var index = 0; index < samples; index++)
            {
                time.Advance(1);
                breaker.RecordSuccess();
            }
            var before = WindowCapacity(breaker);
            time.Advance(samples + 1L);
            var started = Stopwatch.GetTimestamp();
            var ratio = breaker.GetCurrentFailureRatio();
            var expiryMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            var after = WindowCapacity(breaker);
            if (ratio != 0)
                throw new InvalidOperationException("Diagnostic idle expiry failed.");
            rows.Add(new
            {
                samples,
                before,
                after,
                expiryMicroseconds,
                retainedSampleStorageBytes = (long)after * Unsafe.SizeOf<(long, bool)>(),
                notes = "Sample backing storage only, excluding object/array headers; capacity measured after full expiry",
            });
            GC.KeepAlive(breaker);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows,
            new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static int WindowCapacity(CircuitBreaker breaker)
    {
        var field = typeof(CircuitBreaker).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(breaker) is Queue<(long, bool)> queue)
            return queue.EnsureCapacity(0);
        var storage = typeof(CircuitBreaker).GetField("_samplingWindow", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(breaker);
        if (storage?.GetType().GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(storage) is Queue<(long, bool)> samples)
            return samples.EnsureCapacity(0);
        throw new InvalidOperationException("Update the identical baseline/candidate diagnostic harness for the sampling storage layout.");
    }
}
