using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartPipe.Benchmarks;

internal static class RuntimeIoTailReviewReport
{
    public static async Task WriteAsync(string path)
    {
        var rows = new List<object>();
        foreach (var (stages, hasSink, asyncTransform) in new[]
        {
            (1, false, true),
            (4, false, false),
            (1, true, false),
            (16, true, false),
        })
        {
            var benchmark = new RuntimeIoAllocationBenchmarks
            {
                Stages = stages,
                Observers = 1,
                HasSink = hasSink,
                Concurrency = 1,
                AsyncTransform = asyncTransform,
            };
            for (var warmup = 0; warmup < 100; warmup++)
                await benchmark.RunOnceAsync(instrument: false).ConfigureAwait(false);
            for (var repetition = 0; repetition < 9; repetition++)
            {
                var elapsed = await benchmark.RunOnceAsync(instrument: true).ConfigureAwait(false);
                Array.Sort(elapsed!);
                rows.Add(new
                {
                    stages,
                    observers = 1,
                    hasSink,
                    concurrency = 1,
                    asyncTransform,
                    repetition,
                    count = elapsed!.Length,
                    itemP99Microseconds = elapsed[(int)Math.Ceiling(elapsed.Length * 0.99) - 1],
                });
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            framework = RuntimeInformation.FrameworkDescription,
            stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
            warmupRunsPerCase = 100,
            measurement = "Individual item p99; nine repetitions per case after 100 full warmup runs",
            rows,
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }
}
