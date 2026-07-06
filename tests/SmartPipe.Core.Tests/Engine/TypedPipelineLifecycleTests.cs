#nullable enable

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class TypedPipelineLifecycleTests
{
    [Fact]
    public async Task Start_ThrowsOnSecondCall()
    {
        var transformer = new BlockingLifecycleTransformer<int>();
        await using var executor = CreateLifecycleExecutor(
            new CountingLifecycleSource<int>(1),
            transformer);

        var run = executor.Start();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = () => executor.Start();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already been started*");

        await run.CancelAsync();
        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task Start_ThrowsAfterCompletion()
    {
        await using var executor = CreateLifecycleExecutor(
            new CountingLifecycleSource<int>(1, 2, 3),
            new PassThroughLifecycleTransformer<int>());

        var run = executor.Start();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        run.State.Should().Be(PipelineRunState.Completed);

        var act = () => executor.Start();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already been started*");
        await run.DisposeAsync();
    }

    [Fact]
    public async Task Start_ThrowsAfterDispose()
    {
        var transformer = new BlockingLifecycleTransformer<int>();
        await using var executor = CreateLifecycleExecutor(
            new CountingLifecycleSource<int>(1),
            transformer);

        var run = executor.Start();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = run.DisposeAsync().AsTask();
        await transformer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        var act = () => executor.Start();

        act.Should().Throw<ObjectDisposedException>()
            .WithMessage("*pipeline runtime*");
    }

    [Fact]
    public async Task Start_AfterNeverStartedDispose_ShouldThrowObjectDisposedException()
    {
        await using var executor = CreateLifecycleExecutor(
            new CountingLifecycleSource<int>(1),
            new PassThroughLifecycleTransformer<int>());

        await executor.DisposeAsync();

        var act = () => executor.Start();

        act.Should().Throw<ObjectDisposedException>()
            .WithMessage("*pipeline runtime*");
    }

    [Fact]
    public async Task StartDisposeRace_ShouldPublishRunOrRejectStartWithoutPartialState()
    {
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var transformer = new BlockingLifecycleTransformer<int>();
            await using var executor = CreateLifecycleExecutor(
                new CountingLifecycleSource<int>(1),
                transformer);
            using var barrier = new Barrier(2);

            var startTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    return (Run: executor.Start(), Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Run: (PipelineRun<int>?)null, Error: ex);
                }
            });
            var disposeTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await executor.DisposeAsync();
            });

            var start = await startTask.WaitAsync(TimeSpan.FromSeconds(5));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            if (start.Run is not null)
            {
                await FluentActions.Awaiting(() => start.Run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Should().ThrowAsync<OperationCanceledException>();
                start.Run.State.Should().BeOneOf(PipelineRunState.Cancelled, PipelineRunState.Aborted);
            }
            else
            {
                start.Error.Should().BeOfType<ObjectDisposedException>();
            }
        }
    }

    [Fact]
    public async Task Start_AllowsOnlyOneConcurrentCaller()
    {
        var transformer = new BlockingLifecycleTransformer<int>();
        await using var executor = CreateLifecycleExecutor(
            new CountingLifecycleSource<int>(1),
            transformer);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                try
                {
                    return (Run: executor.Start(), Exception: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Run: (PipelineRun<int>?)null, Exception: ex);
                }
            }))
            .ToArray();

        gate.SetResult();

        var results = await Task.WhenAll(attempts);
        var successfulRuns = results
            .Where(result => result.Run is not null)
            .Select(result => result.Run!)
            .ToArray();

        try
        {
            successfulRuns.Should().ContainSingle();
            results.Count(result => result.Exception is InvalidOperationException).Should().Be(63);
            results.Count(result => result.Exception is not null
                && result.Exception is not InvalidOperationException).Should().Be(0);

            var run = successfulRuns.Single();
            await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await run.CancelAsync();
            await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<OperationCanceledException>();
            await run.DisposeAsync();
        }
        finally
        {
            foreach (var run in successfulRuns)
                await run.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_SingleRunLifecycleStillCompletes()
    {
        await using var executor = CreateLifecycleExecutor(
            new CountingLifecycleSource<int>(1, 2, 3),
            new PassThroughLifecycleTransformer<int>());

        var run = executor.Start();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        await run.DisposeAsync();
    }

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

    [Fact]
    public async Task TypedPipeline_CompletionOutcome_IsConsistentWhenSuccessful()
    {
        var observer = new RecordingTerminalObserver();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(new PassThroughLifecycleTransformer<int>())
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        outputs.Should().ContainSingle(output => output.Result.IsSuccess);
        run.Outputs.Completion.IsCompletedSuccessfully.Should().BeTrue();
        observer.TerminalEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineCompletedEvent>();
    }

    [Fact]
    public async Task TypedPipeline_CompletionOutcome_IsConsistentWhenFaulted()
    {
        var observer = new RecordingTerminalObserver();

        var run = PipelineBuilder
            .From(new ThrowingInitializeLifecycleSource<int>())
            .Transform(new PassThroughLifecycleTransformer<int>())
            .WithObserver(observer)
            .Run();

        var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await completion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source initialize boom");

        run.State.Should().Be(PipelineRunState.Faulted);
        await FluentActions.Awaiting(() => run.Outputs.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source initialize boom");
        observer.TerminalEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineFaultedEvent>();
    }

    [Fact]
    public async Task TypedPipeline_CompletionOutcome_IsConsistentWhenCancelled()
    {
        var observer = new RecordingTerminalObserver();
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithObserver(observer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
            })
            .Run();

        await source.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.CancelAsync();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => run.Outputs.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
        observer.TerminalEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineCancelledEvent>();
    }

    [Fact]
    public async Task TypedPipeline_CompletionOutcome_IsConsistentWhenAborted()
    {
        var observer = new RecordingTerminalObserver();
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(transformer)
            .WithObserver(observer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.AbortAsync();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => run.Outputs.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Aborted);
        observer.TerminalEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineCancelledEvent>();
    }

    [Fact]
    public async Task TypedPipeline_CompletionOutcome_IsConsistentWhenCleanupFails()
    {
        var observer = new RecordingTerminalObserver();
        var source = new ThrowingDisposeLifecycleSource<int>(1, "source cleanup boom");

        var run = PipelineBuilder
            .From(source)
            .Transform(new PassThroughLifecycleTransformer<int>())
            .WithObserver(observer)
            .Run();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source cleanup boom");

        run.State.Should().Be(PipelineRunState.Faulted);
        run.Outputs.TryRead(out _).Should().BeTrue();
        await FluentActions.Awaiting(() => run.Outputs.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source cleanup boom");
        observer.TerminalEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineFaultedEvent>();
    }

    [Fact]
    public async Task TypedPipeline_Dispose_AfterProcessingOnlyFailure_ShouldSucceed()
    {
        var run = PipelineBuilder
            .From(new ThrowingInitializeLifecycleSource<int>())
            .Transform(new PassThroughLifecycleTransformer<int>())
            .Run();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source initialize boom");

        await run.DisposeAsync();
    }

    [Fact]
    public async Task TypedPipeline_Dispose_AfterCleanupOnlyFailure_ShouldThrowCleanupFailure()
    {
        var cleanup = new InvalidOperationException("source cleanup boom");
        var source = new ThrowingDisposeLifecycleSource<int>(1, cleanup);

        var run = PipelineBuilder
            .From(source)
            .Transform(new PassThroughLifecycleTransformer<int>())
            .Run();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("source cleanup boom");

        await FluentActions.Awaiting(async () => await run.DisposeAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ReferenceEquals(ex, cleanup));
    }

    [Fact]
    public async Task TypedPipeline_Dispose_AfterProcessingAndCleanupFailure_ShouldThrowCleanupPortionOnly()
    {
        var processing = new InvalidOperationException("source initialize boom");
        var cleanup = new ApplicationException("source cleanup boom");
        var source = new ThrowingInitializeAndDisposeLifecycleSource<int>(processing, cleanup);

        var run = PipelineBuilder
            .From(source)
            .Transform(new PassThroughLifecycleTransformer<int>())
            .Run();

        var completion = await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<AggregateException>();
        completion.Which.InnerExceptions.Should().Equal(processing, cleanup);

        await FluentActions.Awaiting(async () => await run.DisposeAsync())
            .Should().ThrowAsync<ApplicationException>()
            .Where(ex => ReferenceEquals(ex, cleanup));
    }

    [Fact]
    public async Task TypedPipeline_ObserverTerminalFailure_DoesNotRewritePublishedStateOrOutput()
    {
        var observer = new RecordingTerminalObserver();
        var throwingObserver = new ThrowingOnTerminalObserver("observer terminal boom");

        var run = PipelineBuilder
            .From(new CountingLifecycleSource<int>(1))
            .Transform(new PassThroughLifecycleTransformer<int>())
            .WithObserver(observer)
            .WithObserver(
                throwingObserver,
                ObserverReliability.Critical,
                ObserverFailurePolicy.FaultPipeline)
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        run.State.Should().Be(PipelineRunState.Completed);
        outputs.Should().ContainSingle(output => output.Result.IsSuccess);
        run.Outputs.Completion.IsCompletedSuccessfully.Should().BeTrue();
        observer.TerminalEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineCompletedEvent>();
    }

    [Fact]
    public async Task TypedPipeline_Dispose_ConcurrentCallersAwaitSharedTask()
    {
        var source = new BlockingDisposeLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTasks = Enumerable.Range(0, 8)
            .Select(_ => run.DisposeAsync().AsTask())
            .ToArray();

        await source.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disposeTasks.Should().OnlyContain(task => !task.IsCompleted);

        source.ReleaseDispose();

        await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(5));
        source.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task TypedPipeline_Dispose_NeverStartedPerformsComponentCleanup()
    {
        var source = new BlockingAfterFirstLifecycleSource<int>(1);
        var transformer = new BlockingLifecycleTransformer<int>();
        var sink = new TrackingLifecycleSink<int>();
        await using var executor = CreateLifecycleExecutor(source, transformer, sink);

        await executor.DisposeAsync();

        source.DisposeCount.Should().Be(1);
        transformer.DisposeCount.Should().Be(1);
        sink.DisposeCount.Should().Be(1);
    }

    private static TypedPipelineExecutor<int, int> CreateLifecycleExecutor(
        IPipelineSource<int>? source = null,
        IPipelineTransformer<int, int>? transformer = null,
        IPipelineSink<int>? sink = null)
    {
        source ??= new CountingLifecycleSource<int>(1);
        transformer ??= new PassThroughLifecycleTransformer<int>();

        var spec = new TypedPipelineSpec<int, int>(
            "double-start-test-pipeline",
            source,
            [new TypedPipelineStage<int, int>(transformer, 1)]);
        var definition = spec.CreateDefinition(sink);
        var runtime = new PipelineRuntime(PipelineExecutionPlan.Compile(definition));

        return new TypedPipelineExecutor<int, int>(
            runtime,
            spec,
            sink,
            CancellationToken.None);
    }

    private static async Task<PipelineOutput<int>[]> ReadOutputsAsync(
        ChannelReader<PipelineOutput<int>> outputs)
    {
        var results = new List<PipelineOutput<int>>();
        await foreach (var output in outputs.ReadAllAsync().ConfigureAwait(false))
            results.Add(output);

        return results.ToArray();
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

internal sealed class PassThroughLifecycleTransformer<T> : IPipelineTransformer<T, T>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<T>.Success(envelope.Payload));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

internal sealed class ThrowingInitializeLifecycleSource<T> : IPipelineSource<T>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("source initialize boom");

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ThrowingDisposeLifecycleSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T>[] _items;
    private readonly Exception _exception;

    public ThrowingDisposeLifecycleSource(T payload, string message)
        : this(payload, new InvalidOperationException(message))
    {
    }

    public ThrowingDisposeLifecycleSource(T payload, Exception exception)
    {
        _items =
        [
            ProcessingEnvelope<T>.Create(
                payload,
                "cleanup-source",
                "cleanup-run",
                (ulong)Random.Shared.Next(1, int.MaxValue)),
        ];
        _exception = exception;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => throw _exception;
}

internal sealed class ThrowingInitializeAndDisposeLifecycleSource<T> : IPipelineSource<T>
{
    private readonly Exception _initializeException;
    private readonly Exception _disposeException;

    public ThrowingInitializeAndDisposeLifecycleSource(
        Exception initializeException,
        Exception disposeException)
    {
        _initializeException = initializeException;
        _disposeException = disposeException;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default) => throw _initializeException;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    public ValueTask DisposeAsync() => throw _disposeException;
}

internal sealed class BlockingDisposeLifecycleSource<T> : IPipelineSource<T>
{
    private readonly ProcessingEnvelope<T> _item;
    private readonly TaskCompletionSource _releaseDispose =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeCount;

    public BlockingDisposeLifecycleSource(T payload)
    {
        _item = ProcessingEnvelope<T>.Create(
            payload,
            "blocking-dispose-source",
            "blocking-dispose-run",
            (ulong)Random.Shared.Next(1, int.MaxValue));
    }

    public TaskCompletionSource DisposeEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void ReleaseDispose() => _releaseDispose.TrySetResult();

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return _item;
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        DisposeEntered.TrySetResult();
        await _releaseDispose.Task.ConfigureAwait(false);
    }
}

internal sealed class RecordingTerminalObserver : IPipelineObserver
{
    private readonly List<PipelineEvent> _terminalEvents = [];
    private readonly object _gate = new();

    public PipelineEvent[] TerminalEvents
    {
        get
        {
            lock (_gate)
                return _terminalEvents.ToArray();
        }
    }

    public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
    {
        if (pipelineEvent is PipelineCompletedEvent
            or PipelineCancelledEvent
            or PipelineFaultedEvent)
        {
            lock (_gate)
                _terminalEvents.Add(pipelineEvent);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingOnTerminalObserver(string message) : IPipelineObserver
{
    public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
    {
        if (pipelineEvent is PipelineCompletedEvent
            or PipelineCancelledEvent
            or PipelineFaultedEvent)
        {
            throw new InvalidOperationException(message);
        }

        return ValueTask.CompletedTask;
    }
}
