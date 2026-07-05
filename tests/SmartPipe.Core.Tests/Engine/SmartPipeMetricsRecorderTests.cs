#nullable enable

using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class SmartPipeMetricsRecorderTests
{
    [Fact]
    public async Task Metrics_ConcurrentRecordProcessed_ProducesCorrectCounters()
    {
        var recorder = new SmartPipeMetricsRecorder();
        var workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 1_000; i++)
                    recorder.RecordProcessed(2.5);
            }))
            .ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));

        recorder.CaptureSnapshot().ItemsProcessed.Should().Be(8_000);
    }

    [Fact]
    public void Metrics_SnapshotIsImmutable()
    {
        typeof(SmartPipeMetricsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .OnlyContain(property => property.SetMethod == null);
    }

    [Fact]
    public void Metrics_QueueDepthReflectsInputOutputQueues()
    {
        var recorder = new SmartPipeMetricsRecorder();

        recorder.UpdateQueueDepths(inputQueueDepth: 3, outputQueueDepth: 5);

        var snapshot = recorder.CaptureSnapshot();
        snapshot.InputQueueDepth.Should().Be(3);
        snapshot.OutputQueueDepth.Should().Be(5);
    }

    [Fact]
    public void Metrics_LastProcessedUtc_UpdatesAfterSuccess()
    {
        var recorder = new SmartPipeMetricsRecorder();

        recorder.RecordProcessed(10.0);

        recorder.CaptureSnapshot().LastProcessedAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("processed")]
    [InlineData("failed")]
    [InlineData("filtered")]
    [InlineData("input-dropped")]
    [InlineData("output-dropped")]
    [InlineData("dead-lettered")]
    public void Metrics_LastActivityUtc_UpdatesAfterWorkActivity(string activity)
    {
        var recorder = new SmartPipeMetricsRecorder();

        switch (activity)
        {
            case "accepted":
                recorder.RecordActivity();
                break;
            case "processed":
                recorder.RecordProcessed(10.0);
                break;
            case "failed":
                recorder.RecordFailed();
                break;
            case "filtered":
                recorder.RecordFiltered();
                break;
            case "input-dropped":
                recorder.RecordItemDropped();
                break;
            case "output-dropped":
                recorder.RecordOutputDropped();
                break;
            case "dead-lettered":
                recorder.RecordDeadLetter();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unknown activity.");
        }

        recorder.CaptureSnapshot().LastActivityAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Metrics_InputQueueDepthUsesParallelInputChannelCount()
    {
        var transformer = new BlockingTransformer<int>(expectedConcurrentCalls: 2);

        var run = PipelineBuilder
            .From(new EnumerableSource<int>(Enumerable.Range(0, 8).ToArray()))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                InputCapacity = 4,
            })
            .Transform(transformer)
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = await WaitForSnapshotAsync(
            run,
            static snapshot => snapshot.InputQueueDepth > 0);

        snapshot.InputQueueDepth.Should().BePositive();

        transformer.Release();
        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Metrics_OutputQueueDepthUsesOutputChannelCount()
    {
        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1]))
            .Transform(new PassThroughTransformer<int>())
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.Metrics.OutputQueueDepth.Should().Be(1);

        _ = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        run.Metrics.OutputQueueDepth.Should().Be(0);
    }

    [Fact]
    public void Metrics_NoPublicMutableFields()
    {
        typeof(SmartPipeMetrics)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .BeEmpty();
    }

    private static async Task<SmartPipeMetricsSnapshot> WaitForSnapshotAsync<T>(
        PipelineRun<T> run,
        Func<SmartPipeMetricsSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = run.Metrics;
            if (predicate(snapshot))
                return snapshot;

            await Task.Yield();
        }

        throw new TimeoutException("Expected metrics snapshot was not observed.");
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);

        return outputs;
    }

    private sealed class EnumerableSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _payloads;

        public EnumerableSource(IReadOnlyList<T> payloads)
        {
            _payloads = payloads;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _payloads.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _payloads[i],
                    "metrics-recorder-tests",
                    "metrics-recorder-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PassThroughTransformer<T> : IPipelineTransformer<T, T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<T>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly int _expectedConcurrentCalls;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;

        public BlockingTransformer(int expectedConcurrentCalls)
        {
            _expectedConcurrentCalls = expectedConcurrentCalls;
        }

        public TaskCompletionSource ExpectedConcurrentCallsEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeCalls);
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

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
