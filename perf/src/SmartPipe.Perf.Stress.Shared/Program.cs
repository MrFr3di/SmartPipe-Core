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

        StressProfileResult result = options.Profile switch
        {
            "parallel32" => StressProfileResult.FromAggregate(
                options.Profile,
                await RunParallelAsync(32, options.ItemsPerRun).ConfigureAwait(false),
                options.ItemsPerRun),
            "sequential1000" => StressProfileResult.FromAggregate(
                options.Profile,
                await RunSequentialAsync(1_000, options.ItemsPerRun).ConfigureAwait(false),
                options.ItemsPerRun),
            "cancel32" => await RunCancellationProfileAsync(32).ConfigureAwait(false),
            "sourcefailure1000" => await RunSourceFailureProfileAsync(1_000).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown profile '{options.Profile}'."),
        };

        var elapsed = Stopwatch.GetElapsedTime(started);
        var output = new StressResult(
            SchemaVersion: 2,
            Profile: result.Profile,
            Runs: result.Runs,
            ItemsPerRun: options.ItemsPerRun,
            CompletedItems: result.CompletedItems,
            ExpectedItems: result.ExpectedItems,
            Checksum: result.Checksum,
            ExpectedChecksum: result.ExpectedChecksum,
            Errors: result.Errors,
            TerminalRuns: result.TerminalRuns,
            ExpectedTerminalRuns: result.ExpectedTerminalRuns,
            DisposedComponents: result.DisposedComponents,
            ExpectedDisposedComponents: result.ExpectedDisposedComponents,
            LifecycleInvariantPassed: result.LifecycleInvariantPassed,
            ElapsedMilliseconds: elapsed.TotalMilliseconds,
            ProcessWorkingSetBytes: Environment.WorkingSet,
            ThreadPoolThreadCount: ThreadPool.ThreadCount,
            ThreadPoolPendingWorkItems: ThreadPool.PendingWorkItemCount);

        Console.WriteLine(JsonSerializer.Serialize(output));

        return output.Errors == 0 &&
               output.CompletedItems == output.ExpectedItems &&
               output.Checksum == output.ExpectedChecksum &&
               output.TerminalRuns == output.ExpectedTerminalRuns &&
               output.DisposedComponents == output.ExpectedDisposedComponents &&
               output.LifecycleInvariantPassed
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

    private static async Task<StressProfileResult> RunCancellationProfileAsync(int runCount)
    {
        var runs = new PipelineRun<int>[runCount];
        var blockers = new BlockingTransformer[runCount];
        var counters = new LifecycleCounters[runCount];
        var errors = 0L;
        var terminalRuns = 0;

        try
        {
            for (var index = 0; index < runCount; index++)
            {
                var counter = new LifecycleCounters();
                var blocker = new BlockingTransformer(counter);
                counters[index] = counter;
                blockers[index] = blocker;

                runs[index] = PipelineBuilder
                    .From(new SingleItemSource(counter))
                    .Transform(blocker)
                    .WithRuntimeOptions(new PipelineRuntimeOptions
                    {
                        MaxConcurrency = 1,
                        OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
                    })
                    .To(new LifecycleSink(counter));
            }

            await Task.WhenAll(
                    blockers.Select(
                        blocker => blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10))))
                .ConfigureAwait(false);

            await Task.WhenAll(
                    runs.Select(run => run.CancelAsync().AsTask()))
                .ConfigureAwait(false);

            foreach (var run in runs)
            {
                try
                {
                    await run.Completion.ConfigureAwait(false);
                    errors++;
                }
                catch (OperationCanceledException)
                {
                    if (run.State == PipelineRunState.Cancelled)
                        terminalRuns++;
                    else
                        errors++;
                }
                catch
                {
                    errors++;
                }
            }
        }
        finally
        {
            foreach (var run in runs)
            {
                if (run is not null)
                    await run.DisposeAsync().ConfigureAwait(false);
            }
        }

        var disposed = counters.Sum(static counter => counter.DisposedComponents);
        var expectedDisposed = checked((long)runCount * 3);

        return new StressProfileResult(
            Profile: "cancel32",
            Runs: runCount,
            CompletedItems: 0,
            ExpectedItems: 0,
            Checksum: 0,
            ExpectedChecksum: 0,
            Errors: errors,
            TerminalRuns: terminalRuns,
            ExpectedTerminalRuns: runCount,
            DisposedComponents: disposed,
            ExpectedDisposedComponents: expectedDisposed,
            LifecycleInvariantPassed:
                errors == 0 &&
                terminalRuns == runCount &&
                disposed == expectedDisposed);
    }

    private static async Task<StressProfileResult> RunSourceFailureProfileAsync(int runCount)
    {
        var errors = 0L;
        var terminalRuns = 0;
        long disposed = 0;

        for (var index = 0; index < runCount; index++)
        {
            var counter = new LifecycleCounters();

            await using var run = PipelineBuilder
                .From(new DeterministicFailureSource(counter))
                .Transform(new PassthroughTransformer(counter))
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    MaxConcurrency = 1,
                    OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
                })
                .To(new LifecycleSink(counter));

            try
            {
                await run.Completion.ConfigureAwait(false);
                errors++;
            }
            catch (OperationCanceledException)
            {
                errors++;
            }
            catch
            {
                if (run.State == PipelineRunState.Faulted)
                    terminalRuns++;
                else
                    errors++;
            }

            await run.DisposeAsync().ConfigureAwait(false);
            disposed += counter.DisposedComponents;
        }

        var expectedDisposed = checked((long)runCount * 3);

        return new StressProfileResult(
            Profile: "sourcefailure1000",
            Runs: runCount,
            CompletedItems: 0,
            ExpectedItems: 0,
            Checksum: 0,
            ExpectedChecksum: 0,
            Errors: errors,
            TerminalRuns: terminalRuns,
            ExpectedTerminalRuns: runCount,
            DisposedComponents: disposed,
            ExpectedDisposedComponents: expectedDisposed,
            LifecycleInvariantPassed:
                errors == 0 &&
                terminalRuns == runCount &&
                disposed == expectedDisposed);
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

            if (profile is not ("parallel32" or "sequential1000" or "cancel32" or "sourcefailure1000"))
            {
                throw new ArgumentException(
                    "--profile must be parallel32, sequential1000, cancel32, or sourcefailure1000.");
            }

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

    private sealed class SingleItemSource(LifecycleCounters counters) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(1);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            counters.MarkDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeterministicFailureSource(LifecycleCounters counters) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException("smartpipe-perf-deterministic-source-failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync()
        {
            counters.MarkDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughTransformer : IPipelineTransformer<int, int>
    {
        private readonly LifecycleCounters? _counters;

        internal PassthroughTransformer()
        {
        }

        internal PassthroughTransformer(LifecycleCounters counters) =>
            _counters = counters;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync()
        {
            _counters?.MarkDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingTransformer(LifecycleCounters counters) : IPipelineTransformer<int, int>
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Never.Task.WaitAsync(ct).ConfigureAwait(false);
            return StageResult<int>.Success(envelope.Payload);
        }

        public ValueTask DisposeAsync()
        {
            counters.MarkDisposed();
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource Never { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private sealed class LifecycleSink(LifecycleCounters counters) : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            counters.MarkDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LifecycleCounters
    {
        private long _disposedComponents;

        internal long DisposedComponents => Interlocked.Read(ref _disposedComponents);

        internal void MarkDisposed() =>
            Interlocked.Increment(ref _disposedComponents);
    }

    private readonly record struct RunResult(long Count, long Sum, long Errors);
    private readonly record struct AggregateResult(int Runs, long CompletedItems, long Checksum, long Errors);

    private sealed record StressProfileResult(
        string Profile,
        int Runs,
        long CompletedItems,
        long ExpectedItems,
        long Checksum,
        long ExpectedChecksum,
        long Errors,
        int TerminalRuns,
        int ExpectedTerminalRuns,
        long DisposedComponents,
        long ExpectedDisposedComponents,
        bool LifecycleInvariantPassed)
    {
        internal static StressProfileResult FromAggregate(
            string profile,
            AggregateResult aggregate,
            int itemsPerRun) =>
            new(
                Profile: profile,
                Runs: aggregate.Runs,
                CompletedItems: aggregate.CompletedItems,
                ExpectedItems: checked((long)aggregate.Runs * itemsPerRun),
                Checksum: aggregate.Checksum,
                ExpectedChecksum: checked((long)aggregate.Runs * ExpectedSum(itemsPerRun)),
                Errors: aggregate.Errors,
                TerminalRuns: aggregate.Runs,
                ExpectedTerminalRuns: aggregate.Runs,
                DisposedComponents: 0,
                ExpectedDisposedComponents: 0,
                LifecycleInvariantPassed: aggregate.Errors == 0);
    }

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
        int TerminalRuns,
        int ExpectedTerminalRuns,
        long DisposedComponents,
        long ExpectedDisposedComponents,
        bool LifecycleInvariantPassed,
        double ElapsedMilliseconds,
        long ProcessWorkingSetBytes,
        int ThreadPoolThreadCount,
        long ThreadPoolPendingWorkItems);
}
