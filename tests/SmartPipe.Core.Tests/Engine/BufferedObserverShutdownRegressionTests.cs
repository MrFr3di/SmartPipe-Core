using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class BufferedObserverShutdownRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("Cancel", ObserverDispatchMode.BufferedReliable, true, 1)]
    [InlineData("Cancel", ObserverDispatchMode.BufferedReliable, true, 2)]
    [InlineData("Abort", ObserverDispatchMode.BufferedReliable, true, 1)]
    [InlineData("Abort", ObserverDispatchMode.BufferedReliable, true, 2)]
    [InlineData("Dispose", ObserverDispatchMode.BufferedReliable, true, 1)]
    [InlineData("Dispose", ObserverDispatchMode.BufferedReliable, true, 2)]
    [InlineData("Caller", ObserverDispatchMode.BufferedReliable, true, 1)]
    [InlineData("Caller", ObserverDispatchMode.BufferedReliable, true, 2)]
    [InlineData("Fault", ObserverDispatchMode.BufferedReliable, true, 1)]
    [InlineData("Fault", ObserverDispatchMode.BufferedReliable, true, 2)]
    [InlineData("Cancel", ObserverDispatchMode.BufferedBestEffort, true, 1)]
    [InlineData("Cancel", ObserverDispatchMode.BufferedBestEffort, true, 2)]
    [InlineData("Abort", ObserverDispatchMode.BufferedBestEffort, true, 1)]
    [InlineData("Abort", ObserverDispatchMode.BufferedBestEffort, true, 2)]
    [InlineData("Dispose", ObserverDispatchMode.BufferedBestEffort, true, 1)]
    [InlineData("Dispose", ObserverDispatchMode.BufferedBestEffort, true, 2)]
    [InlineData("Caller", ObserverDispatchMode.BufferedBestEffort, true, 1)]
    [InlineData("Caller", ObserverDispatchMode.BufferedBestEffort, true, 2)]
    [InlineData("Fault", ObserverDispatchMode.BufferedBestEffort, true, 1)]
    [InlineData("Fault", ObserverDispatchMode.BufferedBestEffort, true, 2)]
    [InlineData("Cancel", ObserverDispatchMode.BufferedBestEffort, false, 1)]
    [InlineData("Cancel", ObserverDispatchMode.BufferedBestEffort, false, 2)]
    [InlineData("Abort", ObserverDispatchMode.BufferedBestEffort, false, 1)]
    [InlineData("Abort", ObserverDispatchMode.BufferedBestEffort, false, 2)]
    [InlineData("Dispose", ObserverDispatchMode.BufferedBestEffort, false, 1)]
    [InlineData("Dispose", ObserverDispatchMode.BufferedBestEffort, false, 2)]
    [InlineData("Caller", ObserverDispatchMode.BufferedBestEffort, false, 1)]
    [InlineData("Caller", ObserverDispatchMode.BufferedBestEffort, false, 2)]
    [InlineData("Fault", ObserverDispatchMode.BufferedBestEffort, false, 1)]
    [InlineData("Fault", ObserverDispatchMode.BufferedBestEffort, false, 2)]
    public async Task ImmediateStop_CancelsCallbackAndJoinsWorker(
        string action, ObserverDispatchMode mode, bool flush, int concurrency)
    {
        using var caller = new CancellationTokenSource();
        var observer = new HeldObserver();
        var recording = new RecordingObserver();
        var source = new HeldSource();
        var stage = new FaultGateTransformer(action == "Fault");
        var run = PipelineBuilder.From(source).Transform(stage, new StageFailureOptions
        {
            OnPermanentFailure = FailureAction.FaultPipeline,
        })
            .WithObserver(observer, ObserverReliability.Critical, ObserverFailurePolicy.FaultPipeline)
            .WithObserver(recording)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                ObserverDispatch = Options(mode, flush, action == "Fault" ? 16 : 1),
                MaxConcurrency = concurrency,
            }).Run(caller.Token);
        Task? request = null;
        try
        {
            await observer.Entered.Task.WaitAsync(Deadline);
            await source.Entered.Task.WaitAsync(Deadline);
            request = action switch
            {
                "Cancel" => run.CancelAsync().AsTask(),
                "Abort" => run.AbortAsync().AsTask(),
                "Dispose" => run.DisposeAsync().AsTask(),
                "Caller" => caller.CancelAsync(),
                _ => ReleaseFault(stage),
            };
            await observer.Cancelled.Task.WaitAsync(Deadline);
            Assert.False(run.Completion.IsCompleted);
            observer.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            if (action == "Fault")
            {
                Assert.Same(stage.Failure, Assert.IsType<PipelineFailureActionException>(error).InnerException);
                Assert.Equal(PipelineRunState.Faulted, run.State);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Equal(action == "Abort" ? PipelineRunState.Aborted : PipelineRunState.Cancelled, run.State);
            }
            await request.WaitAsync(Deadline);
            Assert.True(observer.Exited.Task.IsCompleted);
            Assert.DoesNotContain(recording.Events, item => item is ObserverFailedEvent);
            if (flush)
            {
                Assert.Single(recording.Events, item => item is PipelineCancelledEvent or PipelineFaultedEvent);
                Assert.Contains(recording.Events, item => item is PipelineStartedEvent);
            }
        }
        finally
        {
            observer.Release.TrySetResult();
            stage.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }

        static Task ReleaseFault(FaultGateTransformer stage)
        {
            stage.Release.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(ObserverDispatchMode.BufferedReliable, true)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort, true)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort, false)]
    public async Task Drain_PreservesUncancelledCallbackUntilReleased(ObserverDispatchMode mode, bool flush)
    {
        var observer = new HeldObserver();
        var source = new HeldSource();
        var run = PipelineBuilder.From(source).Transform(new FaultGateTransformer(false))
            .WithObserver(observer)
            .WithRuntimeOptions(new PipelineRuntimeOptions { ObserverDispatch = Options(mode, flush, 16) }).Run();
        try
        {
            await observer.Entered.Task.WaitAsync(Deadline);
            await source.Entered.Task.WaitAsync(Deadline);
            // Reliable/flush drain waits for delivery. No-flush completion owns worker cancellation at disposal.
            if (flush)
            {
                var drain = run.DrainAsync(Deadline).AsTask();
                await source.Exited.Task.WaitAsync(Deadline);
                Assert.False(observer.Cancelled.Task.IsCompleted);
                Assert.False(run.Completion.IsCompleted);
                observer.Release.TrySetResult();
                await drain.WaitAsync(Deadline);
                Assert.False(observer.Token.IsCancellationRequested);
            }
            else
            {
                observer.Release.TrySetResult();
                await run.DrainAsync(Deadline).AsTask().WaitAsync(Deadline);
            }
            await run.Completion.WaitAsync(Deadline);
            Assert.Equal(PipelineRunState.Completed, run.State);
        }
        finally
        {
            observer.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(ObserverDispatchMode.BufferedReliable)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort)]
    public async Task Dispose_DuringComplete_CancelsBeforeJoiningCompletion(ObserverDispatchMode mode)
    {
        var observer = new HeldObserver();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)], Options(mode, true, 16), SystemPipelineClock.Instance);
        try
        {
            await dispatcher.EmitAsync(new PipelineStartedEvent("test", "run", DateTimeOffset.UtcNow), CancellationToken.None);
            await observer.Entered.Task.WaitAsync(Deadline);
            var complete = dispatcher.CompleteAsync(CancellationToken.None).AsTask();
            var dispose = dispatcher.DisposeAsync().AsTask();
            await observer.Cancelled.Task.WaitAsync(Deadline);
            Assert.False(dispose.IsCompleted);
            var second = dispatcher.DisposeAsync().AsTask();
            Assert.False(second.IsCompleted);
            observer.Release.TrySetResult();
            await Task.WhenAll(complete, dispose, second).WaitAsync(Deadline);
        }
        finally
        {
            observer.Release.TrySetResult();
            await dispatcher.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(ObserverDispatchMode.BufferedReliable)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort)]
    public async Task Dispose_CallbackFailureStillJoinsWorker(ObserverDispatchMode mode)
    {
        var observer = new HeldObserver { ThrowOnCancellation = true };
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer)], Options(mode, true, 16), SystemPipelineClock.Instance);
        await dispatcher.EmitAsync(new PipelineStartedEvent("test", "run", DateTimeOffset.UtcNow), CancellationToken.None);
        await observer.Entered.Task.WaitAsync(Deadline);
        var dispose = dispatcher.DisposeAsync().AsTask();
        try
        {
            await observer.Cancelled.Task.WaitAsync(Deadline);
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            observer.Release.TrySetResult();
        }
        var error = await Record.ExceptionAsync(async () => await dispose.WaitAsync(Deadline));
        var aggregate = Assert.IsType<AggregateException>(error);
        Assert.Contains(aggregate.Flatten().InnerExceptions, item => ReferenceEquals(item, observer.CallbackFailure));
        Assert.True(observer.Exited.Task.IsCompleted);
        var repeated = await Record.ExceptionAsync(async () => await dispatcher.DisposeAsync());
        Assert.Same(error, repeated);
    }

    private static ObserverDispatchOptions Options(ObserverDispatchMode mode, bool flush, int capacity) => new()
    {
        Mode = mode,
        Capacity = capacity,
        FullMode = BoundedChannelFullMode.Wait,
        FailureMode = ObserverFailureMode.UseRegistrationPolicy,
        FlushOnCompletion = flush,
    };

    private sealed class HeldObserver : IPipelineObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public bool ThrowOnCancellation { get; init; }
        public Exception CallbackFailure { get; } = new InvalidOperationException("observer cancellation callback failure");

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (pipelineEvent is not PipelineStartedEvent)
                return;

            Token = ct;
            using var registration = ct.Register(() =>
            {
                Cancelled.TrySetResult();
                if (ThrowOnCancellation)
                    throw CallbackFailure;
            });
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            Exited.TrySetResult();
            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        public ConcurrentQueue<PipelineEvent> Events { get; } = new();
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Events.Enqueue(pipelineEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HeldSource : IPipelineSource<int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                Entered.TrySetResult();
                yield return ProcessingEnvelope<int>.Create(1, "test", "run", 1);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultGateTransformer(bool fault) : IPipelineTransformer<int, int>
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Failure { get; } = new InvalidOperationException("stage failure");
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            if (fault)
            {
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                throw Failure;
            }
            return StageResult<int>.Success(envelope.Payload);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
