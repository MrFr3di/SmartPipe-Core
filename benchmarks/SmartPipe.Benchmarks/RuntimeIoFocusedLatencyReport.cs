using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartPipe.Benchmarks;

internal static class RuntimeIoFocusedLatencyReport
{
    public static async Task WriteAsync(string path)
    {
        var benchmark = new RuntimeIoAllocationBenchmarks
        {
            Stages = 1,
            Observers = 1,
            HasSink = true,
            Concurrency = 1,
            AsyncTransform = false,
        };
        for (var warmup = 0; warmup < 100; warmup++)
            await benchmark.RunOnceAsync(instrument: false).ConfigureAwait(false);
        var rows = new List<object>();
        for (var repetition = 0; repetition < 9; repetition++)
        {
            var elapsed = await benchmark.RunOnceAsync(instrument: true).ConfigureAwait(false);
            Array.Sort(elapsed!);
            rows.Add(new
            {
                repetition,
                count = elapsed!.Length,
                itemP99Microseconds = elapsed[(int)Math.Ceiling(elapsed.Length * 0.99) - 1],
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            framework = RuntimeInformation.FrameworkDescription,
            warmupRuns = 100,
            stages = 1,
            observers = 1,
            hasSink = true,
            concurrency = 1,
            asyncTransform = false,
            measurement = "Individual source-to-sink p99; nine repetitions after 100 full warmup runs",
            rows,
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }
}
