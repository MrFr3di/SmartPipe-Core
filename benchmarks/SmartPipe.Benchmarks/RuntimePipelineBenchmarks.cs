#nullable enable

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Runtime")]
public class RuntimePipelineBenchmarks
{
    [Params(8)]
    public int ItemCount { get; set; }

    [Benchmark(Baseline = true)]
    public async Task Typed_SequentialRuntime_PassthroughTransform()
    {
        await using var run = PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxDegreeOfParallelism = 1,
                OutputMode = PipelineOutputMode.SuppressWhenSinkAttached,
            })
            .ToFactory(_ => new TypedCountingSink());

        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task Typed_BoundedConcurrency_PassthroughTransform()
    {
        await using var run = PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxDegreeOfParallelism = 4,
                OutputMode = PipelineOutputMode.SuppressWhenSinkAttached,
            })
            .ToFactory(_ => new TypedCountingSink());

        await run.Completion.ConfigureAwait(false);
    }

    private sealed class TypedFastSource(int count) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<int>.Create(i);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TypedPassthroughTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TypedCountingSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
