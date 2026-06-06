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
    public Task Legacy_FastSyncSource_PassthroughTransform() =>
        RunLegacyAsync(new LegacyFastSource(ItemCount), new LegacyPassthroughTransformer());

    [Benchmark]
    public Task Legacy_AsyncSource_PassthroughTransform() =>
        RunLegacyAsync(new LegacyYieldingSource(ItemCount), new LegacyPassthroughTransformer());

    [Benchmark]
    public Task Legacy_CpuBoundTransform() =>
        RunLegacyAsync(new LegacyFastSource(ItemCount), new LegacyCpuTransformer());

    [Benchmark]
    public Task Legacy_DelayedAsyncTransform() =>
        RunLegacyAsync(new LegacyFastSource(ItemCount), new LegacyDelayTransformer());

    [Benchmark]
    public Task Legacy_AdaptiveEnabled_PassthroughTransform()
    {
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = Math.Max(16, ItemCount),
            MaxDegreeOfParallelism = 4,
        };
        options.AdaptiveParallelism.Enabled = true;
        options.AdaptiveParallelism.MinDegreeOfParallelism = 1;
        options.AdaptiveParallelism.InitialDegreeOfParallelism = 2;
        options.AdaptiveParallelism.MaxDegreeOfParallelism = 4;
        options.AdaptiveParallelism.InitialInFlightItems = 2;
        options.AdaptiveParallelism.MaxInFlightItems = 8;

        return RunLegacyAsync(
            new LegacyFastSource(ItemCount),
            new LegacyPassthroughTransformer(),
            options);
    }

    [Benchmark]
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

    private static async Task RunLegacyAsync(
        ISource<int> source,
        ITransformer<int, int> transformer,
        SmartPipeChannelOptions? options = null)
    {
        await using var channel = new SmartPipeChannel<int, int>(
            options ?? new SmartPipeChannelOptions
            {
                BoundedCapacity = 64,
                MaxDegreeOfParallelism = 1,
            });
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(new LegacyCountingSink());

        await channel.RunAsync().ConfigureAwait(false);
    }

    private sealed class LegacyFastSource(int count) : ISource<int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<int>> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ProcessingContext<int>(i);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class LegacyYieldingSource(int count) : ISource<int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<int>> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ProcessingContext<int>(i);
                await Task.Yield();
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class LegacyPassthroughTransformer : ITransformer<int, int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<ProcessingResult<int>> TransformAsync(
            ProcessingContext<int> ctx,
            CancellationToken ct = default) =>
            ValueTask.FromResult(ProcessingResult<int>.Success(ctx.Payload, ctx.TraceId));

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class LegacyCpuTransformer : ITransformer<int, int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<ProcessingResult<int>> TransformAsync(
            ProcessingContext<int> ctx,
            CancellationToken ct = default)
        {
            var value = ctx.Payload;
            for (int i = 0; i < 256; i++)
                value = unchecked((value * 31) ^ i);

            return ValueTask.FromResult(ProcessingResult<int>.Success(value, ctx.TraceId));
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class LegacyDelayTransformer : ITransformer<int, int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<int>> TransformAsync(
            ProcessingContext<int> ctx,
            CancellationToken ct = default)
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            return ProcessingResult<int>.Success(ctx.Payload, ctx.TraceId);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class LegacyCountingSink : ISink<int>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ProcessingResult<int> result, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisposeAsync() => Task.CompletedTask;
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
