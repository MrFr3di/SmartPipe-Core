#nullable enable

using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class TypedPipelineLifecycleTests
{
    [Fact]
    public async Task TypedPipeline_Drain_StopsReadingNewSourceItems()
    {
        var transformer = new BlockingLifecycleTransformer<int>();
        var source = new CountingLifecycleSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        drainTask.IsCompleted.Should().BeFalse();

        transformer.Release();

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        source.YieldedCount.Should().Be(1);
        transformer.CompletedCount.Should().Be(1);
    }

    [Fact]
    public async Task TypedPipeline_Drain_FinishesInFlightItems()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        drainTask.IsCompleted.Should().BeFalse();

        transformer.Release();

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        transformer.CompletedCount.Should().Be(1);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task TypedPipeline_Drain_DoesNotSetCancelledOnTimeout()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = async () => await run.DrainAsync(TimeSpan.FromMilliseconds(50));

        await act.Should().ThrowAsync<TimeoutException>();
        run.State.Should().NotBe(PipelineRunState.Cancelled);

        await run.AbortAsync();
    }

    [Fact]
    public async Task TypedPipeline_Drain_TimeoutThrowsTimeoutException()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = async () => await run.DrainAsync(TimeSpan.FromMilliseconds(50));

        await act.Should().ThrowAsync<TimeoutException>();

        await run.AbortAsync();
    }

    [Fact]
    public async Task TypedPipeline_Cancel_CancelsSourceAndWorkers()
    {
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
            })
            .Run();

        await source.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.CancelAsync();

        await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TypedPipeline_Cancel_StateIsCancelled()
    {
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
            })
            .Run();

        await source.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.CancelAsync();

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();

        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task TypedPipeline_Abort_CompletesOutputsWithOperationCanceledException()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.AbortAsync();

        var act = async () => await run.Outputs.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TypedPipeline_Abort_StateIsAborted()
    {
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.AbortAsync();

        run.State.Should().Be(PipelineRunState.Aborted);
    }

    [Fact]
    public async Task TypedPipeline_Dispose_CancelsAndDisposesComponents()
    {
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();
        var sink = new TrackingLifecycleSink<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
            })
            .To(sink);

        await source.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.DisposeAsync();

        await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.DisposeCount.Should().Be(1);
        transformer.DisposeCount.Should().Be(1);
        sink.DisposeCount.Should().Be(1);
    }
}

internal sealed class CountingLifecycleSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;
    private int _yieldedCount;

    public CountingLifecycleSource(params T[] payloads)
    {
        _items = payloads
            .Select(payload => ProcessingEnvelope<T>.Create(
                payload,
                "lifecycle-source",
                "lifecycle-run",
                (ulong)Random.Shared.Next(1, int.MaxValue)))
            .ToArray();
    }

    public int YieldedCount => Volatile.Read(ref _yieldedCount);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _yieldedCount);
            yield return item;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class BlockingAfterFirstLifecycleSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T> _item;
    private int _disposeCount;

    public BlockingAfterFirstLifecycleSource(T payload)
    {
        _item = ProcessingEnvelope<T>.Create(
            payload,
            "lifecycle-source",
            "lifecycle-run",
            (ulong)Random.Shared.Next(1, int.MaxValue));
    }

    public TaskCompletionSource BlockEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource CancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return _item;
        BlockEntered.TrySetResult();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CancellationObserved.TrySetResult();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingLifecycleTransformer<T> : IPipelineTransformer<T, T>
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completedCount;
    private int _disposeCount;

    public TaskCompletionSource Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource CancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CompletedCount => Volatile.Read(ref _completedCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void Release() => _release.TrySetResult();

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default)
    {
        Entered.TrySetResult();

        try
        {
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CancellationObserved.TrySetResult();
            throw;
        }

        Interlocked.Increment(ref _completedCount);
        return StageResult<T>.Success(envelope.Payload);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

internal sealed class TrackingLifecycleSink<T> : IPipelineSink<T>
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}
