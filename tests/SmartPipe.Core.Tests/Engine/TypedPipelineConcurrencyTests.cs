#nullable enable

using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class TypedPipelineConcurrencyTests
{
    [Fact]
    public void MaxDegreeOfParallelism_ShouldDefaultToOne()
    {
        new PipelineRuntimeOptions().MaxDegreeOfParallelism.Should().Be(1);
    }

    [Fact]
    public void MaxDegreeOfParallelism_LessThanOne_ShouldBeRejectedByValidation()
    {
        var options = new PipelineRuntimeOptions { MaxDegreeOfParallelism = 0 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MaxDegreeOfParallelism");
    }

    [Fact]
    public async Task MaxDegreeOfParallelism_One_ShouldProcessSequentially()
    {
        var tracker = new ConcurrencyTracker();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(new TrackingDelayTransformer<int, int>(
                tracker,
                static x => x,
                TimeSpan.FromMilliseconds(20)
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxDegreeOfParallelism = 1 })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(4);
        tracker.MaxObserved.Should().Be(1);
    }

    [Fact]
    public async Task MaxDegreeOfParallelism_ShouldBoundConcurrentEnvelopeProcessing()
    {
        var tracker = new ConcurrencyTracker();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4, 5, 6))
            .Transform(new TrackingDelayTransformer<int, int>(
                tracker,
                static x => x,
                TimeSpan.FromMilliseconds(75)
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxDegreeOfParallelism = 2 })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(6);
        tracker.MaxObserved.Should().Be(2);
    }

    [Fact]
    public async Task ParallelPath_ShouldRunStagesSequentiallyPerEnvelope()
    {
        var order = new ConcurrentDictionary<ulong, ConcurrentQueue<string>>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(new RecordingTransformer<int, int>(order, "stage-1", static x => x + 10))
            .Transform(new RecordingTransformer<int, string>(order, "stage-2", static x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxDegreeOfParallelism = 2 })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(4);
        foreach (var perTrace in order.Values)
            perTrace.ToArray().Should().Equal("stage-1", "stage-2");
    }

    [Fact]
    public async Task ParallelPath_ShouldSerializeSinkWritesByDefault()
    {
        var tracker = new ConcurrencyTracker();
        var sink = new TrackingDelaySink<int>(tracker, TimeSpan.FromMilliseconds(40));

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4, 5, 6))
            .Transform(new TrackingDelayTransformer<int, int>(
                new ConcurrencyTracker(),
                static x => x,
                TimeSpan.FromMilliseconds(20)
            ))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxDegreeOfParallelism = 3 })
            .To(sink);

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        sink.Payloads.Should().HaveCount(6);
        tracker.MaxObserved.Should().Be(1);
    }

    [Fact]
    public async Task StopPipeline_ShouldStopNewAcceptanceAndCompleteAcceptedWork()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, 50).ToArray()))
            .Transform(
                new StopAfterFirstTransformer(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.StopPipeline }
            )
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxDegreeOfParallelism = 2 })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        run.State.Should().Be(PipelineRunState.Completed);
        outputs.Should().NotBeEmpty();
        outputs.Should().HaveCountLessThan(50);
    }

    [Fact]
    public async Task ParallelPath_ShouldPreserveSameTraceStageObserverOrder()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(new TrackingDelayTransformer<int, int>(
                new ConcurrencyTracker(),
                static x => x + 1,
                TimeSpan.FromMilliseconds(20)
            ))
            .Transform(new EnvelopeTransformer<int, string>(static x => x.ToString()))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxDegreeOfParallelism = 2 })
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        var stageEvents = observer.Events
            .OfType<StageStartedEvent>()
            .GroupBy(e => e.TraceId);
        foreach (var perTrace in stageEvents)
            perTrace.Select(e => e.StageId).Should().Equal("stage-1", "stage-2");
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);
        return outputs;
    }
}

internal sealed class ConcurrencyTracker
{
    private int _current;
    private int _maxObserved;

    public int MaxObserved => Volatile.Read(ref _maxObserved);

    public IDisposable Enter()
    {
        var current = Interlocked.Increment(ref _current);
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxObserved);
            if (current <= observed)
                break;
        }
        while (Interlocked.CompareExchange(ref _maxObserved, current, observed) != observed);

        return new ExitHandle(this);
    }

    private void Exit() => Interlocked.Decrement(ref _current);

    private sealed class ExitHandle : IDisposable
    {
        private readonly ConcurrencyTracker _owner;

        public ExitHandle(ConcurrencyTracker owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner.Exit();
    }
}

internal sealed class TrackingDelayTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly ConcurrencyTracker _tracker;
    private readonly Func<TInput, TOutput> _project;
    private readonly TimeSpan _delay;

    public TrackingDelayTransformer(
        ConcurrencyTracker tracker,
        Func<TInput, TOutput> project,
        TimeSpan delay)
    {
        _tracker = tracker;
        _project = project;
        _delay = delay;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        using (_tracker.Enter())
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
            return StageResult<TOutput>.Success(_project(envelope.Payload));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly ConcurrentDictionary<ulong, ConcurrentQueue<string>> _order;
    private readonly string _stageName;
    private readonly Func<TInput, TOutput> _project;

    public RecordingTransformer(
        ConcurrentDictionary<ulong, ConcurrentQueue<string>> order,
        string stageName,
        Func<TInput, TOutput> project)
    {
        _order = order;
        _stageName = stageName;
        _project = project;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        _order.GetOrAdd(envelope.TraceId, _ => new ConcurrentQueue<string>())
            .Enqueue(_stageName);
        return ValueTask.FromResult(StageResult<TOutput>.Success(_project(envelope.Payload)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TrackingDelaySink<T> : IPipelineSink<T>
{
    private readonly ConcurrencyTracker _tracker;
    private readonly TimeSpan _delay;
    private readonly List<T> _payloads = [];

    public TrackingDelaySink(ConcurrencyTracker tracker, TimeSpan delay)
    {
        _tracker = tracker;
        _delay = delay;
    }

    public IReadOnlyList<T> Payloads => _payloads;

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        using (_tracker.Enter())
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
            _payloads.Add(envelope.Payload);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class StopAfterFirstTransformer : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default)
    {
        if (envelope.Payload == 1)
            return StageResult<int>.Failure(new SmartPipeError("stop", ErrorType.Permanent, "StopTest"));

        await Task.Delay(50, ct).ConfigureAwait(false);
        return StageResult<int>.Success(envelope.Payload);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingObserver : IPipelineObserver
{
    private readonly List<PipelineEvent> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<PipelineEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToArray();
        }
    }

    public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
    {
        lock (_gate)
            _events.Add(pipelineEvent);
        return ValueTask.CompletedTask;
    }
}
