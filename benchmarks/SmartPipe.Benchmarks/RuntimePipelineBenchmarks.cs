#nullable enable

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Runtime")]
public class RuntimePipelineBenchmarks
{
    private const int ItemCount = 10_000;
    private readonly SmartPipeMetricsRecorder _metrics = new();

    [Benchmark(Baseline = true)]
    public async Task TypedPipeline_Sequential_10kItems()
    {
        await using var run = CreateSinkBackedRun(maxConcurrency: 1);
        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task TypedPipeline_Parallel_10kItems_MaxConcurrency4()
    {
        await using var run = CreateSinkBackedRun(maxConcurrency: 4);
        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task TypedPipeline_Parallel_10kItems_MaxConcurrency16()
    {
        await using var run = CreateSinkBackedRun(maxConcurrency: 16);
        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task OutputPolicy_SuppressSuccessWhenSinkAttached()
    {
        await using var run = CreateSinkBackedRun(
            maxConcurrency: 4,
            outputPolicy: PipelineOutputPolicy.SuppressAllWhenSinkAttached);
        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task OutputPolicy_EmitAll_WithReader()
    {
        await using var run = PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .Run();

        await DrainOutputsAsync(run.Outputs).ConfigureAwait(false);
        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task StageExecutor_SuccessPath()
    {
        await using var run = PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitFailuresOnly,
            })
            .Run();

        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task StageExecutor_RetryPath()
    {
        await using var run = PipelineBuilder
            .From(new TypedFastSource(ItemCount))
            .Transform(
                new RetryOnceTransformer(),
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, TimeSpan.FromTicks(1)),
                })
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitFailuresOnly,
            })
            .Run();

        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public SmartPipeMetricsSnapshot Metrics_RecordProcessed()
    {
        _metrics.RecordProcessed(1.25);
        return _metrics.CaptureSnapshot();
    }

    [Benchmark]
    public async Task Observer_Inline()
    {
        await using var run = PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitFailuresOnly,
                ObserverDispatch = ObserverDispatchOptions.Inline,
            })
            .WithObserver(new CountingObserver())
            .Run();

        await run.Completion.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task Observer_BufferedBestEffort()
    {
        await using var run = PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitFailuresOnly,
                ObserverDispatch = new ObserverDispatchOptions
                {
                    Mode = ObserverDispatchMode.BufferedBestEffort,
                    Capacity = 1024,
                    FullMode = BoundedChannelFullMode.DropWrite,
                    FlushOnCompletion = false,
                    FailureMode = ObserverFailureMode.Ignore,
                },
            })
            .WithObserver(new CountingObserver())
            .Run();

        await run.Completion.ConfigureAwait(false);
    }

    private static PipelineRun<int> CreateSinkBackedRun(
        int maxConcurrency,
        PipelineOutputPolicy outputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached)
    {
        return PipelineBuilder
            .FromFactory<int>(_ => new TypedFastSource(ItemCount))
            .TransformFactory<int>(_ => new TypedPassthroughTransformer())
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = maxConcurrency,
                OutputPolicy = outputPolicy,
            })
            .ToFactory(_ => new TypedCountingSink());
    }

    private static async Task DrainOutputsAsync<T>(ChannelReader<PipelineOutput<T>> reader)
    {
        await foreach (var _ in reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Intentionally drain benchmark outputs; payload values are not used.
        }
    }

    private sealed class TypedFastSource(int count) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
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

    private sealed class RetryOnceTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(envelope.Attempt == 0
                ? StageResult<int>.Failure(new SmartPipeError("retry", ErrorType.Transient, "Benchmark"))
                : StageResult<int>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TypedCountingSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingObserver : IPipelineObserver
    {
        private long _count;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }
}
