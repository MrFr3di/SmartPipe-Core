using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Perf.Stress;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = StressOptions.Parse(args);
        var started = Stopwatch.GetTimestamp();

        var result = options.Profile switch
        {
            "parallel32" => await RunParallelAsync(32, options.ItemsPerRun).ConfigureAwait(false),
            "sequential1000" => await RunSequentialAsync(1_000, options.ItemsPerRun).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown profile '{options.Profile}'."),
        };

        var elapsed = Stopwatch.GetElapsedTime(started);
        var output = new StressResult(
            SchemaVersion: 1,
            Profile: options.Profile,
            Runs: result.Runs,
            ItemsPerRun: options.ItemsPerRun,
            CompletedItems: result.CompletedItems,
            ExpectedItems: checked((long)result.Runs * options.ItemsPerRun),
            Checksum: result.Checksum,
            ExpectedChecksum: checked((long)result.Runs * ExpectedSum(options.ItemsPerRun)),
            Errors: result.Errors,
            ElapsedMilliseconds: elapsed.TotalMilliseconds,
            ProcessWorkingSetBytes: Environment.WorkingSet,
            ThreadPoolThreadCount: ThreadPool.ThreadCount,
            ThreadPoolPendingWorkItems: ThreadPool.PendingWorkItemCount);

        Console.WriteLine(JsonSerializer.Serialize(output));

        return output.CompletedItems == output.ExpectedItems &&
               output.Checksum == output.ExpectedChecksum &&
               output.Errors == 0
            ? 0
            : 2;
    }

    private static async Task<AggregateResult> RunParallelAsync(int runCount, int itemsPerRun)
    {
        var tasks = Enumerable.Range(0, runCount)
            .Select(_ => ExecuteRunAsync(itemsPerRun))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Aggregate(results);
    }

    private static async Task<AggregateResult> RunSequentialAsync(int runCount, int itemsPerRun)
    {
        var results = new RunResult[runCount];
        for (var index = 0; index < runCount; index++)
            results[index] = await ExecuteRunAsync(itemsPerRun).ConfigureAwait(false);

        return Aggregate(results);
    }

    private static AggregateResult Aggregate(IEnumerable<RunResult> results)
    {
        var runs = 0;
        long completedItems = 0;
        long checksum = 0;
        long errors = 0;

        foreach (var result in results)
        {
            runs++;
            completedItems += result.Count;
            checksum += result.Sum;
            errors += result.Errors;
        }

        return new AggregateResult(runs, completedItems, checksum, errors);
    }

    private static async Task<RunResult> ExecuteRunAsync(int itemCount)
    {
        var sink = new ChecksumSink();

        try
        {
            await using var run = PipelineBuilder
                .From(new FastSource(itemCount))
                .Transform(new PassthroughTransformer())
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    MaxConcurrency = 4,
                    OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
                })
                .To(sink);

            await run.Completion.ConfigureAwait(false);
            return new RunResult(sink.Count, sink.Sum, 0);
        }
        catch
        {
            return new RunResult(sink.Count, sink.Sum, 1);
        }
    }

    private static long ExpectedSum(int count) => checked(((long)count * (count - 1)) / 2);

    private sealed record StressOptions(string Profile, int ItemsPerRun)
    {
        public static StressOptions Parse(string[] args)
        {
            var profile = "parallel32";
            var items = 1_000;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--profile":
                        profile = RequireValue(args, ref index, "--profile");
                        break;
                    case "--items-per-run":
                        var raw = RequireValue(args, ref index, "--items-per-run");
                        if (!int.TryParse(raw, out items) || items <= 0)
                            throw new ArgumentException("--items-per-run must be a positive integer.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (profile is not ("parallel32" or "sequential1000"))
                throw new ArgumentException("--profile must be parallel32 or sequential1000.");

            return new StressOptions(profile, items);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException($"{option} requires a value.");

            return args[index];
        }
    }

    private sealed class FastSource(int count) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<int>.Create(index);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PassthroughTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ChecksumSink : IPipelineSink<int>
    {
        private long _count;
        private long _sum;

        public long Count => Interlocked.Read(ref _count);
        public long Sum => Interlocked.Read(ref _sum);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sum, envelope.Payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly record struct RunResult(long Count, long Sum, long Errors);
    private readonly record struct AggregateResult(int Runs, long CompletedItems, long Checksum, long Errors);

    private sealed record StressResult(
        int SchemaVersion,
        string Profile,
        int Runs,
        int ItemsPerRun,
        long CompletedItems,
        long ExpectedItems,
        long Checksum,
        long ExpectedChecksum,
        long Errors,
        double ElapsedMilliseconds,
        long ProcessWorkingSetBytes,
        int ThreadPoolThreadCount,
        long ThreadPoolPendingWorkItems);
}
