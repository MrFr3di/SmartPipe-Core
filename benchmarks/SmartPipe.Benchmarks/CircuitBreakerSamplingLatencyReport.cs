using System.Diagnostics;
using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

internal static class CircuitBreakerSamplingLatencyReport
{
    internal static async Task WriteAsync(string path, bool bucketed = false)
    {
        const int operationsPerThread = 10_000;
        var rows = new List<object>();
        foreach (var liveSamples in new[] { 1000, 100_000 })
            foreach (var threads in new[] { 1, 8, 32 })
                for (var repetition = 0; repetition < 3; repetition++)
                {
                    var time = new CircuitBreakerSamplingBenchmarks.SamplingTimeProvider();
                    var options = new CircuitBreakerOptions
                    {
                        FailureRatio = 1,
                        SamplingDuration = TimeSpan.FromTicks(liveSamples),
                        MinimumThroughput = 128,
                    };
                    if (bucketed)
                    {
                        // Identical reporter compiles against the merged-F baseline, which has no sampling-mode API.
                        var mode = typeof(CircuitBreakerOptions).GetProperty("SamplingMode")
                            ?? throw new InvalidOperationException("Bucketed sampling is unavailable in this build.");
                        mode.SetValue(options, Enum.Parse(mode.PropertyType, "Bucketed"));
                    }
                    var breaker = new CircuitBreaker(options, time);
                    for (var i = 0; i < liveSamples; i++) { time.Advance(1); breaker.RecordSuccess(); }
                    var latencies = new double[operationsPerThread * threads];
                    using var workers = new CircuitBreakerSamplingBenchmarks.ContentionWorkers(threads, worker =>
                    {
                        for (var i = 0; i < operationsPerThread; i++)
                        {
                            time.Advance(1);
                            var began = Stopwatch.GetTimestamp();
                            var permit = breaker.AcquirePermit();
                            try
                            {
                                if (!permit.IsAllowed) throw new InvalidOperationException("Diagnostic unexpectedly opened breaker.");
                                if ((i + worker) % 16 == 0) permit.RecordFailure(); else permit.RecordSuccess();
                            }
                            finally { permit.Dispose(); }
                            latencies[worker * operationsPerThread + i] = Stopwatch.GetElapsedTime(began).TotalNanoseconds;
                        }
                    });
                    workers.Run(); // JIT/worker warmup; excluded from measured repetition.
                    var contention = Monitor.LockContentionCount;
                    var started = Stopwatch.GetTimestamp();
                    workers.Run();
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    contention = Monitor.LockContentionCount - contention;
                    Array.Sort(latencies);
                    rows.Add(new
                    {
                        liveSamples,
                        threads,
                        repetition,
                        bucketed,
                        operations = latencies.Length,
                        operationsPerSecond = latencies.Length / elapsed,
                        operationP99Nanoseconds = latencies[(int)Math.Ceiling(latencies.Length * 0.99) - 1],
                        processMonitorContentions = contention,
                        ratio = breaker.GetCurrentFailureRatio(),
                        notes = "Individual permit/record/dispose latency, not batch latency; identical timestamp instrumentation in baseline/candidate. Contention count includes process coordination monitors."
                    });
                }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            stopwatchFrequency = Stopwatch.Frequency,
            results = rows,
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }
}
