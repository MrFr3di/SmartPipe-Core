using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Perf.Soak;

internal static class Program
{
    private const int ConcurrentRunsPerBatch = 16;

    public static async Task<int> Main(string[] args)
    {
        SoakOptions options = SoakOptions.Parse(args);
        var counters = new SoakCounters();
        var snapshots = new List<SoakSnapshot>();
        long expectedChecksumPerRun = ExpectedSum(options.ItemsPerRun);

        long started = Stopwatch.GetTimestamp();
        TimeSpan duration = options.Duration;
        TimeSpan snapshotInterval = options.SnapshotInterval;
        TimeSpan nextSnapshot = TimeSpan.Zero;

        while (Stopwatch.GetElapsedTime(started) < duration)
        {
            Task<RunObservation>[] batch = Enumerable.Range(0, ConcurrentRunsPerBatch)
                .Select(_ => ExecuteRunAsync(options.ItemsPerRun, counters))
                .ToArray();

            RunObservation[] results = await Task.WhenAll(batch).ConfigureAwait(false);

            foreach (RunObservation result in results)
            {
                counters.AddRunResult(result);

                if (result.Count != options.ItemsPerRun ||
                    result.Checksum != expectedChecksumPerRun)
                {
                    counters.MarkError();
                }
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= nextSnapshot)
            {
                snapshots.Add(CaptureSnapshot(
                    options.Profile,
                    elapsed,
                    counters));

                nextSnapshot = elapsed + snapshotInterval;
            }

            await Task.Yield();
        }

        TimeSpan finalElapsed = Stopwatch.GetElapsedTime(started);
        SoakSnapshot finalSnapshot = CaptureSnapshot(
            options.Profile,
            finalElapsed,
            counters);
        snapshots.Add(finalSnapshot);

        foreach (SoakSnapshot snapshot in snapshots)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot));
        }

        long created = counters.CreatedComponents;
        long disposed = counters.DisposedComponents;
        long activeRuns = counters.ActiveRuns;
        long completedRuns = counters.CompletedRuns;
        long errors = counters.Errors;

        var final = new SoakResult(
            Kind: "final",
            SchemaVersion: 1,
            Profile: options.Profile,
            DurationSeconds: finalElapsed.TotalSeconds,
            SnapshotIntervalSeconds: options.SnapshotInterval.TotalSeconds,
            ItemsPerRun: options.ItemsPerRun,
            ConcurrentRunsPerBatch: ConcurrentRunsPerBatch,
            CompletedRuns: completedRuns,
            Errors: errors,
            ActiveRuns: activeRuns,
            CreatedComponents: created,
            DisposedComponents: disposed,
            SnapshotCount: snapshots.Count,
            LifecycleInvariantPassed:
                completedRuns > 0 &&
                errors == 0 &&
                activeRuns == 0 &&
                created == disposed);

        Console.WriteLine(JsonSerializer.Serialize(final));

        return final.LifecycleInvariantPassed ? 0 : 2;
    }

    private static async Task<RunObservation> ExecuteRunAsync(
        int itemCount,
        SoakCounters counters)
    {
        counters.RunStarted();

        var source = new TrackedSource(itemCount, counters);
        var transformer = new TrackedTransformer(counters);
        var sink = new TrackedSink(counters);

        try
        {
            await using var run = PipelineBuilder
                .From(source)
                .Transform(transformer)
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    MaxConcurrency = 4,
                    InputCapacity = 32,
                    OutputCapacity = 32,
                    OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
                })
                .To(sink);

            await run.Completion.ConfigureAwait(false);
            return new RunObservation(sink.Count, sink.Checksum);
        }
        catch
        {
            counters.MarkError();
            return new RunObservation(sink.Count, sink.Checksum);
        }
        finally
        {
            counters.RunFinished();
        }
    }

    private static SoakSnapshot CaptureSnapshot(
        string profile,
        TimeSpan elapsed,
        SoakCounters counters)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        GCMemoryInfo gc = GC.GetGCMemoryInfo();

        return new SoakSnapshot(
            Kind: "snapshot",
            SchemaVersion: 1,
            Profile: profile,
            ElapsedSeconds: elapsed.TotalSeconds,
            CompletedRuns: counters.CompletedRuns,
            Errors: counters.Errors,
            ActiveRuns: counters.ActiveRuns,
            CreatedComponents: counters.CreatedComponents,
            DisposedComponents: counters.DisposedComponents,
            ManagedMemoryBytes: GC.GetTotalMemory(forceFullCollection: false),
            GcHeapSizeBytes: gc.HeapSizeBytes,
            GcFragmentedBytes: gc.FragmentedBytes,
            TotalAllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            WorkingSetBytes: process.WorkingSet64,
            CpuTimeMilliseconds: process.TotalProcessorTime.TotalMilliseconds,
            ThreadPoolThreadCount: ThreadPool.ThreadCount,
            ThreadPoolPendingWorkItems: ThreadPool.PendingWorkItemCount,
            HandleOrFdCount: GetHandleOrFdCount(process));
    }

    private static int GetHandleOrFdCount(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return process.HandleCount;

            const string procFd = "/proc/self/fd";
            if (OperatingSystem.IsLinux() && Directory.Exists(procFd))
                return Directory.EnumerateFileSystemEntries(procFd).Count();
        }
        catch
        {
            // Diagnostic-only signal. Unsupported platforms return -1 rather than
            // invalidating correctness/lifecycle evidence.
        }

        return -1;
    }

    private static long ExpectedSum(int count) =>
        checked(((long)count * (count - 1)) / 2);

    private sealed record SoakOptions(
        string Profile,
        int ItemsPerRun,
        TimeSpan Duration,
        TimeSpan SnapshotInterval)
    {
        public static SoakOptions Parse(string[] args)
        {
            string profile = "verify";
            int itemsPerRun = 250;

            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--profile":
                        profile = RequireValue(args, ref index, "--profile");
                        break;

                    case "--items-per-run":
                        string raw = RequireValue(args, ref index, "--items-per-run");
                        if (!int.TryParse(raw, out itemsPerRun) || itemsPerRun <= 0)
                            throw new ArgumentException("--items-per-run must be a positive integer.");
                        break;

                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            return profile switch
            {
                "verify" => new(profile, itemsPerRun, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)),
                "30m" => new(profile, itemsPerRun, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1)),
                "60m" => new(profile, itemsPerRun, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(1)),
                "120m" => new(profile, itemsPerRun, TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(2)),
                _ => throw new ArgumentException("--profile must be verify, 30m, 60m, or 120m."),
            };
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException($"{option} requires a value.");

            return args[index];
        }
    }

    private sealed class TrackedSource : IPipelineSource<int>
    {
        private readonly int _count;
        private readonly SoakCounters _counters;

        internal TrackedSource(int count, SoakCounters counters)
        {
            _count = count;
            _counters = counters;
            _counters.ComponentCreated();
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int index = 0; index < _count; index++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<int>.Create(index);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _counters.ComponentDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackedTransformer : IPipelineTransformer<int, int>
    {
        private readonly SoakCounters _counters;

        internal TrackedTransformer(SoakCounters counters)
        {
            _counters = counters;
            _counters.ComponentCreated();
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync()
        {
            _counters.ComponentDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackedSink : IPipelineSink<int>
    {
        private readonly SoakCounters _counters;
        private long _count;
        private long _checksum;

        internal TrackedSink(SoakCounters counters)
        {
            _counters = counters;
            _counters.ComponentCreated();
        }

        internal long Count => Interlocked.Read(ref _count);
        internal long Checksum => Interlocked.Read(ref _checksum);

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _checksum, envelope.Payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _counters.ComponentDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SoakCounters
    {
        private long _completedRuns;
        private long _errors;
        private long _activeRuns;
        private long _createdComponents;
        private long _disposedComponents;

        internal long CompletedRuns => Interlocked.Read(ref _completedRuns);
        internal long Errors => Interlocked.Read(ref _errors);
        internal long ActiveRuns => Interlocked.Read(ref _activeRuns);
        internal long CreatedComponents => Interlocked.Read(ref _createdComponents);
        internal long DisposedComponents => Interlocked.Read(ref _disposedComponents);

        internal void RunStarted() =>
            Interlocked.Increment(ref _activeRuns);

        internal void RunFinished() =>
            Interlocked.Decrement(ref _activeRuns);

        internal void AddRunResult(RunObservation _) =>
            Interlocked.Increment(ref _completedRuns);

        internal void MarkError() =>
            Interlocked.Increment(ref _errors);

        internal void ComponentCreated() =>
            Interlocked.Increment(ref _createdComponents);

        internal void ComponentDisposed() =>
            Interlocked.Increment(ref _disposedComponents);
    }

    private readonly record struct RunObservation(long Count, long Checksum);

    private sealed record SoakSnapshot(
        string Kind,
        int SchemaVersion,
        string Profile,
        double ElapsedSeconds,
        long CompletedRuns,
        long Errors,
        long ActiveRuns,
        long CreatedComponents,
        long DisposedComponents,
        long ManagedMemoryBytes,
        long GcHeapSizeBytes,
        long GcFragmentedBytes,
        long TotalAllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long WorkingSetBytes,
        double CpuTimeMilliseconds,
        int ThreadPoolThreadCount,
        long ThreadPoolPendingWorkItems,
        int HandleOrFdCount);

    private sealed record SoakResult(
        string Kind,
        int SchemaVersion,
        string Profile,
        double DurationSeconds,
        double SnapshotIntervalSeconds,
        int ItemsPerRun,
        int ConcurrentRunsPerBatch,
        long CompletedRuns,
        long Errors,
        long ActiveRuns,
        long CreatedComponents,
        long DisposedComponents,
        int SnapshotCount,
        bool LifecycleInvariantPassed);
}
