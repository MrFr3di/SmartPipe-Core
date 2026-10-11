using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("RuntimeIo")]
public class RuntimeIoAllocationBenchmarks
{
    internal const int ItemCount = 10_000;

    [Params(1, 4, 16)]
    public int Stages { get; set; }

    [Params(0, 1)]
    public int Observers { get; set; }

    [Params(false, true)]
    public bool HasSink { get; set; }

    [Params(1, 8, 32)]
    public int Concurrency { get; set; }

    [Params(false, true)]
    public bool AsyncTransform { get; set; }

    [Benchmark(OperationsPerInvoke = ItemCount)]
    public async Task ProcessItems() => await RunOnceAsync(instrument: false).ConfigureAwait(false);

    internal async Task<double[]?> RunOnceAsync(bool instrument)
    {
        var starts = instrument ? new long[ItemCount] : null;
        var latencies = instrument ? new double[ItemCount] : null;
        var sink = new VerifyingSink(Stages, starts, latencies);
        var observer = new VerifyingObserver();
        var source = PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            ValueTask.FromResult<IPipelineSource<int>>(new Source(starts)));
        var first = PipelineDefinitionBuilder.From(new PipelineKey("runtime-io-benchmark"), source)
            .WithLineageMode(LineageMode.Off)
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = Concurrency })
            .Transform(new PipelineStageKey("stage-0"), TransformComponent());
        for (var index = 1; index < Stages; index++)
            first = first.Transform(new PipelineStageKey($"stage-{index}"), TransformComponent());
        if (Observers != 0)
            first = first.WithObserver(observer);
        var definition = HasSink
            ? first.To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSink<int>>(sink)))
            : first.Build();

        await using var run = await definition.StartAsync().ConfigureAwait(false);
        var outputCount = 0;
        long outputChecksum = 0;
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        {
            if (!output.Result.IsSuccess || output.Envelope is null)
                throw new InvalidOperationException("The success benchmark emitted a non-success item.");
            var value = output.Envelope.Payload;
            outputCount++;
            outputChecksum += value;
            if (latencies is not null && starts is not null)
                latencies[value - Stages] = Stopwatch.GetElapsedTime(starts[value - Stages]).TotalMicroseconds;
        }
        await run.Completion.ConfigureAwait(false);
        var expectedChecksum = ((long)ItemCount * (ItemCount - 1) / 2) + ((long)ItemCount * Stages);
        if (run.State != PipelineRunState.Completed
            || outputCount != (HasSink ? 0 : ItemCount)
            || (!HasSink && outputChecksum != expectedChecksum)
            || sink.Count != (HasSink ? ItemCount : 0)
            || (HasSink && sink.Checksum != expectedChecksum)
            || (Observers != 0 && observer.Successes != (long)ItemCount * Stages))
            throw new InvalidOperationException("Benchmark item count, checksum, sink, observer or terminal state mismatch.");
        return latencies;
    }

    private PipelineComponent<IPipelineTransformer<int, int>> TransformComponent() =>
        PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
            ValueTask.FromResult<IPipelineTransformer<int, int>>(new Transformer(AsyncTransform)));

    private sealed class Source(long[]? starts) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var index = 0; index < ItemCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                var envelope = ProcessingEnvelope<int>.Create(index);
                if (starts is not null)
                    starts[index] = Stopwatch.GetTimestamp();
                yield return envelope;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Transformer(bool asynchronous) : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return asynchronous
                ? TransformSuspendedAsync(envelope, ct)
                : ValueTask.FromResult(StageResult<int>.Success(envelope.Payload + 1));
        }

        private static async ValueTask<StageResult<int>> TransformSuspendedAsync(ProcessingEnvelope<int> envelope, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return StageResult<int>.Success(envelope.Payload + 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VerifyingSink(int stages, long[]? starts, double[]? latencies) : IPipelineSink<int>
    {
        public long Count;
        public long Checksum;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            Interlocked.Add(ref Checksum, envelope.Payload);
            if (latencies is not null && starts is not null)
                latencies[envelope.Payload - stages] = Stopwatch.GetElapsedTime(starts[envelope.Payload - stages]).TotalMicroseconds;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VerifyingObserver : IPipelineObserver
    {
        public long Successes;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (pipelineEvent is StageSucceededEvent)
                Interlocked.Increment(ref Successes);
            return ValueTask.CompletedTask;
        }
    }
}

internal static class RuntimeIoLatencyReport
{
    public static async Task WriteAsync(string path)
    {
        var rows = new List<object>();
        foreach (var stages in new[] { 1, 4, 16 })
            foreach (var observers in new[] { 0, 1 })
                foreach (var hasSink in new[] { false, true })
                    foreach (var concurrency in new[] { 1, 8, 32 })
                        foreach (var asyncTransform in new[] { false, true })
                        {
                            var benchmark = new RuntimeIoAllocationBenchmarks
                            {
                                Stages = stages,
                                Observers = observers,
                                HasSink = hasSink,
                                Concurrency = concurrency,
                                AsyncTransform = asyncTransform,
                            };
                            await benchmark.RunOnceAsync(instrument: false).ConfigureAwait(false);
                            for (var repetition = 0; repetition < 3; repetition++)
                            {
                                var elapsed = await benchmark.RunOnceAsync(instrument: true).ConfigureAwait(false);
                                Array.Sort(elapsed!);
                                rows.Add(new
                                {
                                    stages,
                                    observers,
                                    hasSink,
                                    concurrency,
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
            os = RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            stopwatchFrequency = Stopwatch.Frequency,
            measurement = "Source emission to sink write or output-reader consumption; three per-run item p99 values, not batch p99",
            rows,
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }
}
