using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Perf.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Comparative", "Core", "StrictAB")]
public class CorePipelineAbBenchmarks
{
    [Params(1_000, 100_000)]
    public int ItemCount { get; set; }

    [Params(1, 4, 16)]
    public int MaxConcurrency { get; set; }

    [GlobalSetup]
    public async Task VerifyCorrectnessAsync()
    {
        const int verificationCount = 1_000;
        var actual = await ExecuteAsync(verificationCount, maxConcurrency: 4).ConfigureAwait(false);
        var expected = new ChecksumSnapshot(
            verificationCount,
            ((long)verificationCount * (verificationCount - 1)) / 2);

        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Core A/B correctness precheck failed. Expected {expected}, got {actual}.");
        }
    }

    [Benchmark]
    public Task<ChecksumSnapshot> SourceTransformSink() =>
        ExecuteAsync(ItemCount, MaxConcurrency);

    private static async Task<ChecksumSnapshot> ExecuteAsync(int itemCount, int maxConcurrency)
    {
        var sink = new ChecksumSink();

        await using var run = PipelineBuilder
            .From(new FastSource(itemCount))
            .Transform(new PassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = maxConcurrency,
                OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
            })
            .To(sink);

        await run.Completion.ConfigureAwait(false);
        return sink.Capture();
    }

    public readonly record struct ChecksumSnapshot(long Count, long Sum);

    private sealed class FastSource(int count) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

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
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

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

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sum, envelope.Payload);
            return ValueTask.CompletedTask;
        }

        public ChecksumSnapshot Capture() =>
            new(Interlocked.Read(ref _count), Interlocked.Read(ref _sum));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
