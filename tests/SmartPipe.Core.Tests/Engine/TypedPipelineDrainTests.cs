#nullable enable
#pragma warning disable CS0618 // These tests cover compatibility aliases.

using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

/// <summary>
/// P2B: Tests for typed sequential drain with source-boundary semantics.
/// </summary>
public sealed class TypedPipelineDrainTests
{
    [Fact]
    public async Task DrainAsync_ShouldStopAcceptingNewSourceItemsAtEnvelopeBoundary()
    {
        // Arrange: source with two items, blocks stage after first accepted item.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int moveNextAttempts = 0;
        var source = new BarrierGateControlledSource<int>(barrier, () => Interlocked.Increment(ref moveNextAttempts), 1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new BarrierTransformer<int, int>(barrier))
            .Run();

        // Wait for first item to be yielded.
        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: drain while first item is still stuck in the barrier.
        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        Func<Task> waitForDrainBeforeRelease = async () =>
            await drainTask.WaitAsync(TimeSpan.FromMilliseconds(150));
        await waitForDrainBeforeRelease.Should().ThrowAsync<TimeoutException>(
            "graceful drain must wait for already accepted work to complete");

        // Assert: DrainAsync has requested drain — first accepted item must still complete.
        // Release the barrier so the first item finishes.
        barrier.TrySetResult();

        await drainTask;
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: only the first item was requested from source.
        var outputs = await ReadOutputsAsync(run.Outputs);
        outputs.Should().HaveCount(1);
        outputs[0].Result.IsSuccess.Should().BeTrue();

        // Assert: second source item was not requested (MoveNextAsync only called once).
        Volatile.Read(ref moveNextAttempts).Should().Be(1,
            "source-boundary drain must not request a second item after drain was requested");
    }

    [Fact]
    public async Task DrainAsync_CompletesAcceptedWork()
    {
        var source = new CompletionSignalingSource<int>(1, 2, 3);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.DrainAsync(TimeSpan.FromSeconds(5));
        await run.Completion;

        // All three items should have been processed (source completed naturally before drain).
        var outputs = await ReadOutputsAsync(run.Outputs);
        outputs.Should().HaveCount(3);
    }

    [Fact]
    public async Task DrainAsync_AfterCompletion_ShouldNotRegressRunState()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        run.State.Should().Be(PipelineRunState.Completed);

        await run.DrainAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task TryDrainAsync_Completed_ReturnsCompleted()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BarrierGateControlledSource<int>(barrier, () => 0, 1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new BarrierTransformer<int, int>(barrier))
            .Run();

        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drainTask = run.TryDrainAsync(TimeSpan.FromSeconds(5)).AsTask();

        barrier.TrySetResult();

        var result = await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        result.Status.Should().Be(PipelineDrainStatus.Completed);
        result.State.Should().Be(PipelineRunState.Completed);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task TryDrainAsync_CancelsSourceRead_ButFinishesAcceptedItems()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BarrierGateControlledSource<int>(barrier, () => 0, 1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new BarrierTransformer<int, int>(barrier))
            .Run();

        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.TryDrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        drainTask.IsCompleted.Should().BeFalse("accepted in-flight work must finish before drain completes");

        barrier.TrySetResult();

        var result = await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs);

        result.Status.Should().BeOneOf(
            PipelineDrainStatus.Completed,
            PipelineDrainStatus.AlreadyCompleted);
        result.State.Should().Be(PipelineRunState.Completed);
        outputs.Select(output => output.Result.Value).Should().Equal(1);
    }

    [Fact]
    public async Task TryDrainAsync_SourceBlockedInMoveNext_ReturnsPredictably()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int moveNextAttempts = 0;
        var source = new GateAfterFirstSource<int>(gate, () => Interlocked.Increment(ref moveNextAttempts), 1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.SecondMoveNextEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await run.TryDrainAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs);

        result.Status.Should().BeOneOf(
            PipelineDrainStatus.Completed,
            PipelineDrainStatus.AlreadyCompleted);
        result.State.Should().Be(PipelineRunState.Completed);
        outputs.Select(output => output.Result.Value).Should().Equal(1);
        Volatile.Read(ref moveNextAttempts).Should().Be(1);
    }

    [Fact]
    public async Task TryDrainAsync_InFlightStageCompletes()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.TryDrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        drainTask.IsCompleted.Should().BeFalse();
        transformer.Release();

        var result = await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        result.Status.Should().Be(PipelineDrainStatus.Completed);
        transformer.CompletedCount.Should().Be(1);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task TryDrainAsync_Timeout_ReturnsTimedOutStillRunning()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await run.TryDrainAsync(TimeSpan.FromMilliseconds(50));

        result.Status.Should().Be(PipelineDrainStatus.TimedOutStillRunning);
        result.State.Should().NotBe(PipelineRunState.Cancelled);

        await run.AbortAsync();
    }

    [Fact]
    public async Task DrainAsync_Timeout_ThrowsButRunCanStillBeCancelled()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainAct = async () => await run.DrainAsync(TimeSpan.FromMilliseconds(50));
        await drainAct.Should().ThrowAsync<TimeoutException>();

        await run.CancelAsync();
        var completionAct = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await completionAct.Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task CancelAsync_CancelsSourceAndWorkers()
    {
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 2 })
            .Run();

        await source.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.CancelAsync();

        await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completionAct = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await completionAct.Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task AbortAsync_CancelsSourceAndWorkersImmediately()
    {
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 2 })
            .Run();

        await source.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.AbortAsync();

        await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.State.Should().Be(PipelineRunState.Aborted);
    }

    [Fact]
    public async Task DrainThenCancel_TransitionsPredictably()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(30)).AsTask();
        drainTask.IsCompleted.Should().BeFalse();

        await run.CancelAsync();

        var drainAct = async () => await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await drainAct.Should().ThrowAsync<OperationCanceledException>();
        var completionAct = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await completionAct.Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task TryDrainAsync_Faulted_ReturnsFaulted()
    {
        var source = new EnvelopeSource<int>(1);
        var transformer = new GateFailingTransformer<int, int>(ErrorType.Permanent);

        var run = PipelineBuilder
            .From(source)
            .Transform(
                transformer,
                new StageFailureOptions { OnPermanentFailure = FailureAction.FaultPipeline })
            .Run();

        await transformer.TransformEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drainTask = run.TryDrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        transformer.Release();

        var result = await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Status.Should().Be(PipelineDrainStatus.Faulted);
        result.State.Should().Be(PipelineRunState.Faulted);
        result.Exception.Should().BeOfType<PipelineFailureActionException>();
    }

    [Fact]
    public async Task TryDrainAsync_AlreadyCompleted_ReturnsAlreadyCompleted()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await run.TryDrainAsync(TimeSpan.FromSeconds(5));

        result.Status.Should().Be(PipelineDrainStatus.AlreadyCompleted);
        result.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task DrainAsync_Timeout_ShouldThrowTimeoutException()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: drain with very short timeout.
        var act = async () => await run.DrainAsync(TimeSpan.FromMilliseconds(100));

        // Assert: timeout throws TimeoutException.
        await act.Should().ThrowAsync<TimeoutException>();

        // Assert: timeout did not mark the run as aborted.
        run.State.Should().NotBe(PipelineRunState.Aborted);

        await run.AbortAsync();
    }

    [Fact]
    public async Task DrainAsync_WhenSourceIsBlockedInMoveNextAsync_ShouldCancelSourceRead()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int moveNextAttempts = 0;
        var source = new GateAfterFirstSource<int>(gate, () => Interlocked.Increment(ref moveNextAttempts), 1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.SecondMoveNextEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Start drain — but source is blocked on MoveNextAsync for item 2.
        await run.DrainAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var outputs = await ReadOutputsAsync(run.Outputs);
        outputs.Should().HaveCount(1,
            "source-boundary drain should cancel source reads that are blocked in MoveNextAsync");
        Volatile.Read(ref moveNextAttempts).Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_ShouldPreserveCompletionTaskFaultState()
    {
        var source = new EnvelopeSource<int>(1);
        var transformer = new GateFailingTransformer<int, int>(ErrorType.Permanent);

        var run = PipelineBuilder
            .From(source)
            .Transform(
                transformer,
                new StageFailureOptions { OnPermanentFailure = FailureAction.FaultPipeline }
            )
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputMode = PipelineOutputMode.SuppressAll,
            })
            .Run();

        await transformer.TransformEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drainAct = async () => await run.DrainAsync(TimeSpan.FromSeconds(5));
        transformer.Release();

        await drainAct.Should().ThrowAsync<PipelineFailureActionException>();

        // The run should have faulted due to the failing transformer.
        run.State.Should().Be(PipelineRunState.Faulted);

        var act = async () => await run.Completion;
        await act.Should().ThrowAsync<PipelineFailureActionException>();
    }

    [Fact]
    public async Task DrainAsync_WithBufferedInput_ShouldCompleteAcceptedBufferedItems()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ThresholdDrainSource<int>(
            Enumerable.Range(1, 10),
            emittedThreshold: 6);

        var run = PipelineBuilder
            .From(source)
            .Transform(new BarrierTransformer<int, int>(barrier))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                InputCapacity = 3,
            })
            .Run();

        await source.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        Func<Task> waitForDrainBeforeRelease = async () =>
            await drainTask.WaitAsync(TimeSpan.FromMilliseconds(150));
        await waitForDrainBeforeRelease.Should().ThrowAsync<TimeoutException>(
            "drain must finish accepted buffered work before completing");

        barrier.TrySetResult();

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var outputs = await ReadOutputsAsync(run.Outputs);
        outputs.Select(output => output.Result.Value).Should().BeEquivalentTo(Enumerable.Range(1, 5));
        source.EmittedCount.Should().Be(6,
            "source-boundary drain can cancel the source read that crossed the threshold before accepting it");
    }

    [Fact]
    public async Task DrainAsync_WithCancellation_ShouldRespectCancellationToken()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await run.DrainAsync(TimeSpan.FromSeconds(30), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await run.AbortAsync();
    }

    [Fact]
    public async Task AbortAsync_ShouldCompleteImmediately_DifferingFromGracefulDrain()
    {
        var neverComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int moveNextAttempts = 0;
        var source = new NeverCompletingSource<int>(neverComplete, () => Interlocked.Increment(ref moveNextAttempts));

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Abort should not wait for source to cooperate.
        await run.AbortAsync();

        run.State.Should().Be(PipelineRunState.Aborted);

        neverComplete.TrySetResult();
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

/// <summary>
/// Source that blocks after yielding the first item through a barrier.
/// Tracks MoveNextAsync call count.
/// First item passes through unimpeded. Subsequent items wait on the barrier.
/// </summary>
internal sealed class BarrierGateControlledSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;
    private readonly TaskCompletionSource _barrier;
    private readonly Func<int> _moveNextCounter;

    public BarrierGateControlledSource(
        TaskCompletionSource barrier,
        Func<int> moveNextCounter,
        params T[] payloads)
    {
        _barrier = barrier;
        _moveNextCounter = moveNextCounter;
        _items = payloads
            .Select(payload =>
                ProcessingEnvelope<T>.Create(
                    payload,
                    "source-pipeline",
                    "source-run",
                    (ulong)Random.Shared.Next(1, int.MaxValue)
                )
            )
            .ToArray();
    }

    public TaskCompletionSource FirstItemYielded { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _moveNextCounter();
        FirstItemYielded.TrySetResult();
        yield return _items[0];

        // Wait on barrier before yielding remaining items.
        await _barrier.Task.WaitAsync(ct).ConfigureAwait(false);

        for (var i = 1; i < _items.Length; i++)
        {
            _moveNextCounter();
            ct.ThrowIfCancellationRequested();
            yield return _items[i];
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Source that blocks indefinitely after yielding the first item.
/// </summary>
internal sealed class NeverCompletingSource<T> : IPipelineSource<T>
{
    private readonly TaskCompletionSource _release;
    private readonly Func<int> _moveNextCounter;
    private readonly ProcessingEnvelope<T> _firstItem;

    public NeverCompletingSource(TaskCompletionSource release, Func<int> moveNextCounter, params T[] payloads)
    {
        _release = release;
        _moveNextCounter = moveNextCounter;
        _firstItem = ProcessingEnvelope<T>.Create(
            payloads.Length > 0 ? payloads[0] : default!,
            "source-pipeline",
            "source-run",
            (ulong)Random.Shared.Next(1, int.MaxValue)
        );
    }

    public TaskCompletionSource FirstItemYielded { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _moveNextCounter();
        FirstItemYielded.TrySetResult();
        yield return _firstItem;

        // Block indefinitely waiting for release.
        await _release.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CompletionSignalingSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;

    public CompletionSignalingSource(params T[] payloads)
    {
        _items = payloads
            .Select(payload =>
                ProcessingEnvelope<T>.Create(
                    payload,
                    "source-pipeline",
                    "source-run",
                    (ulong)Random.Shared.Next(1, int.MaxValue)
                )
            )
            .ToArray();
    }

    public TaskCompletionSource Completed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }

        Completed.TrySetResult();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ThresholdDrainSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;
    private readonly int _emittedThreshold;
    private int _emittedCount;

    public ThresholdDrainSource(IEnumerable<T> payloads, int emittedThreshold)
    {
        _emittedThreshold = emittedThreshold;
        _items = payloads
            .Select((payload, index) =>
                ProcessingEnvelope<T>.Create(
                    payload,
                    "source-pipeline",
                    "source-run",
                    (ulong)(index + 1)))
            .ToArray();
    }

    public TaskCompletionSource ThresholdReached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int EmittedCount => Volatile.Read(ref _emittedCount);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
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

/// <summary>
/// Source that yields the first item, then blocks on MoveNextAsync for subsequent items.
/// Used to test the "source blocked inside MoveNextAsync" contract.
/// </summary>
internal sealed class GateAfterFirstSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;
    private readonly TaskCompletionSource _gate;
    private readonly Func<int> _moveNextCounter;

    public GateAfterFirstSource(
        TaskCompletionSource gate,
        Func<int> moveNextCounter,
        params T[] payloads)
    {
        _gate = gate;
        _moveNextCounter = moveNextCounter;
        _items = payloads
            .Select(payload =>
                ProcessingEnvelope<T>.Create(
                    payload,
                    "source-pipeline",
                    "source-run",
                    (ulong)Random.Shared.Next(1, int.MaxValue)
                )
            )
            .ToArray();
    }

    public TaskCompletionSource FirstItemYielded { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource SecondMoveNextEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _moveNextCounter();
        FirstItemYielded.TrySetResult();
        yield return _items[0];

        // Block on MoveNextAsync for subsequent items.
        SecondMoveNextEntered.TrySetResult();
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);

        for (var i = 1; i < _items.Length; i++)
        {
            _moveNextCounter();
            ct.ThrowIfCancellationRequested();
            yield return _items[i];
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Transformer that blocks on a barrier until it is released.
/// </summary>
internal sealed class BarrierTransformer<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
{
    private readonly TaskCompletionSource _barrier;

    public BarrierTransformer(TaskCompletionSource barrier)
    {
        _barrier = barrier;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        // Block until barrier is released.
        await _barrier.Task.WaitAsync(ct).ConfigureAwait(false);

        // Cast the payload through — for int->int this is a no-op conceptually.
        if (typeof(TInput) == typeof(TOutput))
            return StageResult<TOutput>.Success((TOutput)(object)envelope.Payload!);

        throw new NotSupportedException("BarrierTransformer requires TInput == TOutput");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Transformer that always returns a failure.
/// Used to verify drain preserves fault state.
/// </summary>
internal sealed class DrainFailingTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly ErrorType _errorType;

    public DrainFailingTransformer(ErrorType errorType = ErrorType.Permanent)
    {
        _errorType = errorType;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(
            StageResult<TOutput>.Failure(
                new SmartPipeError("drain-test-failure", _errorType, "TestFailure")
            )
        );
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class GateFailingTransformer<TInput, TOutput>
    : IPipelineTransformer<TInput, TOutput>
{
    private readonly ErrorType _errorType;
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GateFailingTransformer(ErrorType errorType = ErrorType.Permanent)
    {
        _errorType = errorType;
    }

    public TaskCompletionSource TransformEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default)
    {
        TransformEntered.TrySetResult();
        await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        return StageResult<TOutput>.Failure(
            new SmartPipeError("drain-test-failure", _errorType, "TestFailure")
        );
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
