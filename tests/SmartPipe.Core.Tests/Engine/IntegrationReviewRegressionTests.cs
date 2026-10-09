using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class IntegrationReviewRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task TerminalFault_WithFullReliableQueue_CancelsCallbackBeforeNotification(int concurrency, bool taskFault)
    {
        var observer = new GatedObserver();
        var stage = new GatedTransformer();
        var run = PipelineBuilder.From(new SingleSource()).Transform(stage, new StageFailureOptions
        {
            OnPermanentFailure = FailureAction.FaultPipeline,
            ExceptionClassifier = taskFault ? _ => throw stage.Failure : null,
        }).WithObserver(observer).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            MaxConcurrency = concurrency,
            ObserverDispatch = new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.BufferedReliable,
                Capacity = 1,
                FlushOnCompletion = true,
            },
        }).Run();
        try
        {
            await observer.Entered.Task.WaitAsync(Deadline);
            // Stage entry proves StageStarted was accepted into the sole queue slot.
            await stage.Entered.Task.WaitAsync(Deadline);
            stage.Release.TrySetResult();
            await observer.Cancelled.Task.WaitAsync(Deadline);
            Assert.False(run.Completion.IsCompleted);
            observer.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Contains(Flatten(error), item => ReferenceEquals(item, stage.Failure));
            Assert.Equal(PipelineRunState.Faulted, run.State);
            Assert.True(observer.Exited.Task.IsCompleted);
            Assert.False(stage.DisposedWhileActive);
        }
        finally
        {
            observer.Release.TrySetResult();
            stage.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await run.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExternalStage_AfterFinalizationBudget_DisposalJoinsAndReportsLateFault(bool scopeOwned, bool taskFault)
    {
        var time = new ObservedTimeProvider();
        var stage = new GatedTransformer();
        var descriptor = scopeOwned
            ? PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>((_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(stage))
            : PipelineComponent.Borrowed<IPipelineTransformer<int, int>>(stage);
        var definition = PipelineDefinitionBuilder.From(new PipelineKey("external-stage"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new SingleSource())))
            .Transform(new PipelineStageKey("held"), descriptor, new StageFailureOptions
            {
                ExceptionClassifier = taskFault ? _ => throw stage.Failure : null,
                Timeout = new TimeoutPolicy
                {
                    AttemptTimeout = AttemptTimeout,
                    LateAttemptFinalizationTimeout = FinalizationTimeout,
                },
            }).Build();
        var run = await definition.StartAsync(new PipelineActivationContext(definition.Key, Guid.NewGuid(), new EmptyServices(), time));
        try
        {
            await stage.Entered.Task.WaitAsync(Deadline);
            await time.AttemptTimer.Task.WaitAsync(Deadline);
            await run.CancelAsync().AsTask().WaitAsync(Deadline);
            await stage.Cancelled.Task.WaitAsync(Deadline);
            await time.FinalizationTimer.Task.WaitAsync(Deadline);
            time.Advance(FinalizationTimeout);
            var completionError = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Contains(Flatten(completionError), item => item is TimeoutException);
            var disposal = run.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, stage.DisposeCount);
            stage.Release.TrySetResult();
            var disposalError = await Record.ExceptionAsync(async () => await disposal.WaitAsync(Deadline));
            Assert.Single(Flatten(disposalError), item => ReferenceEquals(item, stage.Failure));
            Assert.True(stage.Exited.Task.IsCompleted);
            Assert.Equal(0, stage.DisposeCount);
            Assert.Same(completionError, await Record.ExceptionAsync(async () => await run.Completion));
            Assert.Same(disposalError, await Record.ExceptionAsync(async () => await run.DisposeAsync()));
        }
        finally
        {
            stage.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await run.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
    }

    [Theory]
    [InlineData(FailureAction.EmitFailureResult, false)]
    [InlineData(FailureAction.Skip, false)]
    [InlineData(FailureAction.StopPipeline, false)]
    [InlineData(FailureAction.FaultPipeline, true)]
    public async Task NonFatalOutcome_PreservesObserverToken(FailureAction action, bool recoveredRetry)
    {
        var observer = new TokenObserver();
        var stage = new RecoveringTransformer(recoveredRetry);
        var retryPredicates = 0;
        var run = PipelineBuilder.From(new SingleSource()).Transform(stage, new StageFailureOptions
        {
            OnPermanentFailure = action,
            OnRetryExhausted = action,
            Retry = recoveredRetry ? new RetryPolicy(maxRetries: 1, delay: TimeSpan.FromMilliseconds(1), retryOn: _ =>
            {
                retryPredicates++;
                return true;
            }) : null,
        }).WithObserver(observer).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            ObserverDispatch = new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.BufferedReliable,
                Capacity = 1,
                FlushOnCompletion = true,
            },
        }).Run();
        try
        {
            await run.Completion.WaitAsync(Deadline);
            Assert.Equal(PipelineRunState.Completed, run.State);
            Assert.False(observer.Token.IsCancellationRequested);
            Assert.Equal(recoveredRetry ? 1 : 0, retryPredicates);
            Assert.Equal(recoveredRetry ? 2 : 1, stage.Attempts);
        }
        finally
        {
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryPredicateFault_PreservesStageFailedEventAndOriginalFailure(bool buffered)
    {
        var fault = new InvalidOperationException("retry predicate failed");
        var held = new GatedObserver { HoldOnStarted = buffered };
        var recorder = new RecordingObserver();
        var stage = new GatedFailureResultTransformer();
        var run = PipelineBuilder.From(new SingleSource()).Transform(stage, new StageFailureOptions
        {
            Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.FromTicks(1), retryOn: _ => throw fault),
            OnPermanentFailure = FailureAction.FaultPipeline,
        }).WithObserver(held).WithObserver(recorder).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            ObserverDispatch = new ObserverDispatchOptions
            {
                Mode = buffered ? ObserverDispatchMode.BufferedReliable : ObserverDispatchMode.Inline,
                Capacity = 1,
                FullMode = BoundedChannelFullMode.Wait,
                FlushOnCompletion = true,
            },
        }).Run();
        try
        {
            if (buffered)
                await held.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await stage.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            stage.Release.TrySetResult();
            if (buffered)
            {
                await held.Cancelled.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
                held.Release.TrySetResult();
            }
            var error = await Record.ExceptionAsync(async () =>
                await run.Completion.WaitAsync(Deadline, TestContext.Current.CancellationToken));
            Assert.Contains(Flatten(error), item => ReferenceEquals(item, fault));
            Assert.Equal(PipelineRunState.Faulted, run.State);
            Assert.Single(recorder.Events, item => item is StageFailedEvent);
        }
        finally
        {
            stage.Release.TrySetResult();
            held.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () =>
                await run.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task TerminalStageFault_WithThrowingObserverCancellation_PreservesPrimaryAndCallbackFailure()
    {
        var held = new GatedObserver { ThrowOnCancellation = true };
        var stage = new GatedFailureResultTransformer();
        var run = PipelineBuilder.From(new SingleSource()).Transform(stage, new StageFailureOptions
        {
            OnPermanentFailure = FailureAction.FaultPipeline,
        }).WithObserver(held).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            ObserverDispatch = new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.BufferedReliable,
                Capacity = 1,
                FullMode = BoundedChannelFullMode.Wait,
                FlushOnCompletion = true,
            },
        }).Run();
        try
        {
            await held.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await stage.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            stage.Release.TrySetResult();
            await held.Cancelled.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            held.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () =>
                await run.Completion.WaitAsync(Deadline, TestContext.Current.CancellationToken));
            Assert.Contains(Flatten(error), item => item is PipelineFailureActionException);
            Assert.Contains(Flatten(error), item => ReferenceEquals(item, held.CallbackFailure));
            Assert.Equal(PipelineRunState.Faulted, run.State);
        }
        finally
        {
            held.Release.TrySetResult();
            stage.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () =>
                await run.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SinkFailure_WithFullReliableQueue_StopsCallbackBeforeFailureNotification()
    {
        var held = new GatedSinkStartObserver();
        var recorder = new RecordingObserver();
        var sink = new GatedThrowingSink();
        var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(held), new PipelineObserverRegistration(recorder)],
            new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.BufferedReliable,
                Capacity = 1,
                FullMode = BoundedChannelFullMode.Wait,
                FlushOnCompletion = true,
            },
            SystemPipelineClock.Instance);
        using var sinkExecutor = new SinkExecutor<int>(
            sink, "pipeline", "run", SystemPipelineClock.Instance,
            dispatcher.EmitAsync, stopCallbacksOnFaultAsync: dispatcher.StopCallbacksAsync);
        try
        {
            var write = sinkExecutor.WriteAsync(ProcessingEnvelope<int>.Create(1), CancellationToken.None).AsTask();
            await held.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await sink.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            // While SinkWriteStarted is held in the observer callback, fill the
            // sole queue slot so SinkWriteFailed must wait for cancellation.
            await dispatcher.EmitAsync(
                new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            sink.Release.TrySetResult();
            await held.Cancelled.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            held.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () =>
                await write.WaitAsync(Deadline, TestContext.Current.CancellationToken));
            Assert.Same(sink.Failure, error);
            await dispatcher.FlushAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
            Assert.Single(recorder.Events, item => item is SinkWriteFailedEvent);
        }
        finally
        {
            held.Release.TrySetResult();
            sink.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () =>
                await dispatcher.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken));
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

    private sealed class GatedFailureResultTransformer : IPipelineTransformer<int, int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return StageResult<int>.Failure(new SmartPipeError("stage rejected", ErrorType.Transient));
        }
    }

    private sealed class GatedThrowingSink : IPipelineSink<int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Failure { get; } = new IOException("sink failed");
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            throw Failure;
        }
    }

    private sealed class GatedSinkStartObserver : IPipelineObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (pipelineEvent is not SinkWriteStartedEvent)
                return ValueTask.CompletedTask;
            return HoldAsync(ct);
        }
        private async ValueTask HoldAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class TokenObserver : IPipelineObserver
    {
        public CancellationToken Token { get; private set; }
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Token = ct;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecoveringTransformer(bool recover) : IPipelineTransformer<int, int>
    {
        public int Attempts { get; private set; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Attempts++;
            return ValueTask.FromResult(recover && Attempts > 1
                ? StageResult<int>.Success(envelope.Payload)
                : StageResult<int>.Failure(new SmartPipeError("retryable stage failure", ErrorType.Transient)));
        }
    }

    private static IEnumerable<Exception> Flatten(Exception? error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten),
        PipelineFailureActionException failure => new[] { failure }.Concat(Flatten(failure.Error.InnerException)),
        null => [],
        _ => new[] { error }.Concat(Flatten(error.InnerException)),
    };

    private sealed class SingleSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(1);
            await Task.CompletedTask;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class GatedTransformer : IPipelineTransformer<int, int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Failure { get; } = new IOException("held stage failure");
        public int DisposeCount { get; private set; }
        public bool DisposedWhileActive { get; private set; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            try
            {
                await Release.Task.ConfigureAwait(false);
                throw Failure;
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposedWhileActive |= !Exited.Task.IsCompleted;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedObserver : IPipelineObserver
    {
        public bool HoldOnStarted { get; init; } = true;
        public bool ThrowOnCancellation { get; init; }
        public Exception CallbackFailure { get; } = new InvalidOperationException("observer cancellation callback fault");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (!HoldOnStarted || pipelineEvent is not PipelineStartedEvent)
                return;
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

    private sealed class ObservedTimeProvider : FakeTimeProvider
    {
        public TaskCompletionSource AttemptTimer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalizationTimer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == AttemptTimeout)
                AttemptTimer.TrySetResult();
            if (dueTime == FinalizationTimeout)
                FinalizationTimer.TrySetResult();
            return timer;
        }
    }
}
