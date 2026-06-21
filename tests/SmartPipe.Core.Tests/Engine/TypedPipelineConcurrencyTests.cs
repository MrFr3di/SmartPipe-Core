using System.Collections.Concurrent;
#pragma warning disable CS0618 // These tests cover compatibility aliases.
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class TypedPipelineConcurrencyTests
{
    [Fact]
    public async Task TypedPipeline_MaxConcurrency1_ProcessesAllItems()
    {
        var source = new TestEnvelopeSource<int>(Enumerable.Range(1, 25));

        var run = PipelineBuilder
            .From(source)
            .Transform(new TestEnvelopeTransformer<int, int>(x => x * 2))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 1 })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().Equal(Enumerable.Range(1, 25).Select(x => x * 2));
    }

    [Fact]
    public async Task MaxConcurrency_ControlsWorkerCount()
    {
        var source = new TestEnvelopeSource<int>(Enumerable.Range(1, 12));
        var transformer = new BlockingTrackingTransformer<int>(expectedConcurrentCalls: 3);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 3,
                InputCapacity = 3,
            })
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().Be(3);

        transformer.Release();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().BeEquivalentTo(Enumerable.Range(1, 12));
        transformer.ProcessedCounts.Should().HaveCount(12);
        transformer.ProcessedCounts.Values.Should().OnlyContain(x => x == 1);
    }

    [Fact]
    public async Task MaxConcurrency4_ProcessesConcurrently()
    {
        var source = new TestEnvelopeSource<int>(Enumerable.Range(1, 40));
        var transformer = new BlockingTrackingTransformer<int>(expectedConcurrentCalls: 4);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                InputCapacity = 4,
            })
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().Be(4);

        transformer.Release();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().BeEquivalentTo(Enumerable.Range(1, 40));
        transformer.ProcessedCounts.Should().HaveCount(40);
        transformer.ProcessedCounts.Values.Should().OnlyContain(x => x == 1);
    }

    [Fact]
    public async Task TypedPipeline_MaxConcurrency4_DoesNotDuplicateItems()
    {
        var source = new TestEnvelopeSource<int>(Enumerable.Range(1, 200));
        var transformer = new CountingTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                InputCapacity = 16,
            })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().BeEquivalentTo(Enumerable.Range(1, 200));
        outputs.Select(x => x.Result.Value).Should().OnlyHaveUniqueItems();
        transformer.ProcessedCounts.Should().HaveCount(200);
        transformer.ProcessedCounts.Values.Should().OnlyContain(x => x == 1);
    }

    [Fact]
    public async Task TypedPipeline_BoundedInput_AppliesBackpressure()
    {
        var source = new TestEnvelopeSource<int>(Enumerable.Range(1, 20));
        var transformer = new BlockingTrackingTransformer<int>(expectedConcurrentCalls: 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                InputCapacity = 1,
            })
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.EmittedCount.Should().BeLessThan(20);
        source.EmittedCount.Should().BeLessThanOrEqualTo(4);

        transformer.Release();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().BeEquivalentTo(Enumerable.Range(1, 20));
    }

    [Fact]
    public async Task TypedPipeline_InputCapacity_GreaterThanMaxConcurrency_IsHonored()
    {
        var source = new ThresholdEnvelopeSource<int>(
            Enumerable.Range(1, 20),
            emittedThreshold: 9);
        var transformer = new BlockingTrackingTransformer<int>(expectedConcurrentCalls: 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                InputCapacity = 8,
                OutputMode = PipelineOutputMode.SuppressAll,
            })
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.EmittedCount.Should().BeGreaterThanOrEqualTo(9);

        transformer.Release();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        transformer.ProcessedCounts.Should().HaveCount(20);
        transformer.ProcessedCounts.Values.Should().OnlyContain(x => x == 1);
    }

    [Fact]
    public async Task TypedPipeline_SourceException_FaultsRun()
    {
        var exception = new InvalidOperationException("source failed");
        var source = new ThrowingEnvelopeSource<int>(exception);

        var run = PipelineBuilder
            .From(source)
            .Transform(new TestEnvelopeTransformer<int, int>(x => x))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                OutputMode = PipelineOutputMode.SuppressAll,
            })
            .Run();

        var act = async () => await run.Completion;
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source failed");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task TypedPipeline_WorkerException_UsesFailurePolicy()
    {
        var exception = new InvalidOperationException("worker failed");
        var source = new TestEnvelopeSource<int>(Enumerable.Range(1, 10));

        var run = PipelineBuilder
            .From(source)
            .Transform(new ThrowingEnvelopeTransformer<int, int>(2, exception))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                InputCapacity = 4,
                OutputMode = PipelineOutputMode.SuppressAll,
            })
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        run.Metrics.ItemsFailed.Should().BeGreaterThan(0);
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);
        return outputs;
    }

    private sealed class TestEnvelopeSource<T> : IPipelineSource<T>
    {
        private readonly ProcessingEnvelope<T>[] _items;
        private int _emittedCount;

        public TestEnvelopeSource(IEnumerable<T> payloads)
        {
            _items = payloads
                .Select((payload, index) =>
                    ProcessingEnvelope<T>.Create(
                        payload,
                        "test-pipeline",
                        "test-run",
                        (ulong)(index + 1)))
                .ToArray();
        }

        public int EmittedCount => Volatile.Read(ref _emittedCount);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _emittedCount);
                yield return item;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThresholdEnvelopeSource<T> : IPipelineSource<T>
    {
        private readonly ProcessingEnvelope<T>[] _items;
        private readonly int _emittedThreshold;
        private int _emittedCount;

        public ThresholdEnvelopeSource(IEnumerable<T> payloads, int emittedThreshold)
        {
            _emittedThreshold = emittedThreshold;
            _items = payloads
                .Select((payload, index) =>
                    ProcessingEnvelope<T>.Create(
                        payload,
                        "test-pipeline",
                        "test-run",
                        (ulong)(index + 1)))
                .ToArray();
        }

        public TaskCompletionSource ThresholdReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EmittedCount => Volatile.Read(ref _emittedCount);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                var emitted = Interlocked.Increment(ref _emittedCount);
                if (emitted >= _emittedThreshold)
                    ThresholdReached.TrySetResult();

                yield return item;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingEnvelopeSource<T> : IPipelineSource<T>
    {
        private readonly Exception _exception;

        public ThrowingEnvelopeSource(Exception exception)
        {
            _exception = exception;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw _exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestEnvelopeTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly Func<TInput, TOutput> _transform;

        public TestEnvelopeTransformer(Func<TInput, TOutput> transform)
        {
            _transform = transform;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(StageResult<TOutput>.Success(_transform(envelope.Payload)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingTransformer<T> : IPipelineTransformer<T, T>
        where T : notnull
    {
        private readonly ConcurrentDictionary<T, int> _processedCounts = [];

        public IReadOnlyDictionary<T, int> ProcessedCounts => _processedCounts;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            _processedCounts.AddOrUpdate(envelope.Payload, 1, (_, count) => count + 1);
            await Task.Yield();
            return StageResult<T>.Success(envelope.Payload);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTrackingTransformer<T> : IPipelineTransformer<T, T>
        where T : notnull
    {
        private readonly int _expectedConcurrentCalls;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<T, int> _processedCounts = [];
        private int _activeCalls;
        private int _maxObservedConcurrency;

        public BlockingTrackingTransformer(int expectedConcurrentCalls)
        {
            _expectedConcurrentCalls = expectedConcurrentCalls;
        }

        public TaskCompletionSource ExpectedConcurrentCallsEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public IReadOnlyDictionary<T, int> ProcessedCounts => _processedCounts;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            _processedCounts.AddOrUpdate(envelope.Payload, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaxObservedConcurrency(active);
            if (active >= _expectedConcurrentCalls)
                ExpectedConcurrentCallsEntered.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                return StageResult<T>.Success(envelope.Payload);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void UpdateMaxObservedConcurrency(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxObservedConcurrency);
                if (active <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, active, current) == current)
                    return;
            }
        }
    }

    private sealed class ThrowingEnvelopeTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly TInput _throwOnPayload;
        private readonly Exception _exception;

        public ThrowingEnvelopeTransformer(TInput throwOnPayload, Exception exception)
        {
            _throwOnPayload = throwOnPayload;
            _exception = exception;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default)
        {
            if (EqualityComparer<TInput>.Default.Equals(envelope.Payload, _throwOnPayload))
                throw _exception;

            return ValueTask.FromResult(StageResult<TOutput>.Success((TOutput)(object)envelope.Payload!));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
