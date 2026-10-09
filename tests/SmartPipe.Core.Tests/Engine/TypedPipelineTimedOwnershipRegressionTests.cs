using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class TypedPipelineTimedOwnershipRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData("Cancel", 1, false, false)]
    [InlineData("Cancel", 1, false, true)]
    [InlineData("Cancel", 1, true, false)]
    [InlineData("Cancel", 1, true, true)]
    [InlineData("Cancel", 2, false, false)]
    [InlineData("Cancel", 2, false, true)]
    [InlineData("Cancel", 2, true, false)]
    [InlineData("Cancel", 2, true, true)]
    [InlineData("Abort", 1, false, false)]
    [InlineData("Abort", 1, false, true)]
    [InlineData("Abort", 1, true, false)]
    [InlineData("Abort", 1, true, true)]
    [InlineData("Abort", 2, false, false)]
    [InlineData("Abort", 2, false, true)]
    [InlineData("Abort", 2, true, false)]
    [InlineData("Abort", 2, true, true)]
    [InlineData("Dispose", 1, false, false)]
    [InlineData("Dispose", 1, false, true)]
    [InlineData("Dispose", 1, true, false)]
    [InlineData("Dispose", 1, true, true)]
    [InlineData("Dispose", 2, false, false)]
    [InlineData("Dispose", 2, false, true)]
    [InlineData("Dispose", 2, true, false)]
    [InlineData("Dispose", 2, true, true)]
    public async Task CallerCancellation_RetainsAttemptUntilCleanupCompletes(string action, int concurrency, bool duringGrace, bool failCleanup)
    {
        var time = new ObservedTimeProvider();
        var stage = new HeldTransformer(failCleanup);
        var run = CreateRun(stage, time, Timeout.InfiniteTimeSpan, concurrency);
        try
        {
            await stage.Entered.Task.WaitAsync(Deadline);
            await time.AttemptTimer.Task.WaitAsync(Deadline);
            if (duringGrace)
            {
                time.Advance(AttemptTimeout);
                await stage.CancellationObserved.Task.WaitAsync(Deadline);
            }
            var request = action switch
            {
                "Abort" => run.AbortAsync().AsTask(),
                "Dispose" => run.DisposeAsync().AsTask(),
                _ => run.CancelAsync().AsTask(),
            };
            if (action != "Dispose")
                await request.WaitAsync(Deadline);
            await stage.CancellationObserved.Task.WaitAsync(Deadline);
            var signal = await Task.WhenAny(time.FinalizationTimer.Task, stage.Disposed.Task).WaitAsync(Deadline);
            Assert.Same(time.FinalizationTimer.Task, signal);
            Assert.False(stage.Disposed.Task.IsCompleted);
            Assert.False(run.Completion.IsCompleted);
            stage.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            if (failCleanup)
            {
                Assert.Contains(Flatten(error), failure => ReferenceEquals(failure, stage.Failure));
                Assert.Equal(PipelineRunState.Faulted, run.State);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Equal(action == "Abort" ? PipelineRunState.Aborted : PipelineRunState.Cancelled, run.State);
            }
            await request.WaitAsync(Deadline);
            Assert.True(stage.Exited.Task.IsCompleted);
            Assert.False(stage.DisposedWhileActive);
            Assert.Equal(1, stage.DisposeCount);
        }
        finally
        {
            stage.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task CancellationBudgetExpired_DefersDisposalAndReportsLaterCleanupFault()
    {
        var time = new ObservedTimeProvider();
        var stage = new HeldTransformer(failCleanup: true);
        var run = CreateRun(stage, time, TimeSpan.Zero);
        try
        {
            await stage.Entered.Task.WaitAsync(Deadline);
            await time.AttemptTimer.Task.WaitAsync(Deadline);
            await run.CancelAsync().AsTask().WaitAsync(Deadline);
            var signal = await Task.WhenAny(time.FinalizationTimer.Task, stage.Disposed.Task).WaitAsync(Deadline);
            Assert.Same(time.FinalizationTimer.Task, signal);
            time.Advance(FinalizationTimeout);
            var completionError = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Contains(Flatten(completionError), error => error is TimeoutException);
            Assert.False(stage.Disposed.Task.IsCompleted);
            var disposal = run.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            stage.Release.TrySetResult();
            var disposeError = await Record.ExceptionAsync(async () => await disposal.WaitAsync(Deadline));
            Assert.Contains(Flatten(disposeError), error => ReferenceEquals(error, stage.Failure));
            Assert.Same(completionError, await Record.ExceptionAsync(async () => await run.Completion));
            Assert.False(stage.DisposedWhileActive);
            Assert.Equal(1, stage.DisposeCount);
        }
        finally
        {
            stage.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await run.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
    }

    [Fact]
    public async Task InfiniteGrace_WaitsForCooperativeResultInsteadOfDetaching()
    {
        var time = new ObservedTimeProvider();
        var stage = new HeldTransformer(failCleanup: false);
        var run = CreateRun(stage, time, Timeout.InfiniteTimeSpan);
        try
        {
            await stage.Entered.Task.WaitAsync(Deadline);
            await time.AttemptTimer.Task.WaitAsync(Deadline);
            time.Advance(AttemptTimeout);
            await stage.CancellationObserved.Task.WaitAsync(Deadline);
            stage.Release.TrySetResult();
            var output = await run.Outputs.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.True(output.Result.IsSuccess);
            await run.Completion.WaitAsync(Deadline);
            Assert.False(time.FinalizationTimer.Task.IsCompleted);
            Assert.False(stage.DisposedWhileActive);
        }
        finally
        {
            stage.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingTimeoutCallback_RetainsExecutionAndBothFailures(bool failCleanup)
    {
        var time = new ObservedTimeProvider();
        var callbackFailure = new InvalidOperationException("timeout cancellation callback failed");
        var stage = new HeldTransformer(failCleanup) { CallbackFailure = callbackFailure };
        var run = CreateRun(stage, time, TimeSpan.Zero);
        try
        {
            await stage.Entered.Task.WaitAsync(Deadline);
            await time.AttemptTimer.Task.WaitAsync(Deadline);
            time.Advance(AttemptTimeout);
            var signal = await Task.WhenAny(time.FinalizationTimer.Task, stage.Disposed.Task).WaitAsync(Deadline);
            Assert.Same(time.FinalizationTimer.Task, signal);
            Assert.False(stage.Disposed.Task.IsCompleted);
            stage.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Contains(Flatten(error), failure => ReferenceEquals(failure, callbackFailure));
            if (failCleanup)
                Assert.Contains(Flatten(error), failure => ReferenceEquals(failure, stage.Failure));
            Assert.Equal(PipelineRunState.Faulted, run.State);
            Assert.False(stage.DisposedWhileActive);
            Assert.Equal(1, stage.DisposeCount);
        }
        finally
        {
            stage.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposeCallbackFailure_StillJoinsAndDisposesDeferredStage(bool sourceCallback, bool failCleanup)
    {
        var time = new ObservedTimeProvider();
        var callbackFailure = new InvalidOperationException("dispose cancellation callback failed");
        var stage = new HeldTransformer(failCleanup) { CallbackFailure = sourceCallback ? null : callbackFailure };
        var source = sourceCallback
            ? (IPipelineSource<int>)new ThrowingCancellationSource(callbackFailure)
            : new EnumerablePipelineSource<int>([1]);
        var run = PipelineBuilder.From(source).Transform(stage, new StageFailureOptions
        {
            Timeout = new TimeoutPolicy
            {
                AttemptTimeout = AttemptTimeout,
                LateAttemptFinalizationTimeout = FinalizationTimeout,
            },
        }).WithRuntimeOptions(new PipelineRuntimeOptions { Clock = new TimeProviderPipelineClock(time) }).Run();
        try
        {
            await stage.Entered.Task.WaitAsync(Deadline);
            await time.AttemptTimer.Task.WaitAsync(Deadline);
            var disposal = run.DisposeAsync().AsTask();
            var signal = await Task.WhenAny(time.FinalizationTimer.Task, disposal).WaitAsync(Deadline);
            Assert.Same(time.FinalizationTimer.Task, signal);
            Assert.False(disposal.IsCompleted);
            time.Advance(FinalizationTimeout);
            var completionError = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Contains(Flatten(completionError), error => error is TimeoutException);
            Assert.False(disposal.IsCompleted);
            Assert.False(stage.Disposed.Task.IsCompleted);
            stage.Release.TrySetResult();
            var disposeError = await Record.ExceptionAsync(async () => await disposal.WaitAsync(Deadline));
            Assert.Contains(Flatten(completionError).Concat(Flatten(disposeError)), error => ReferenceEquals(error, callbackFailure));
            if (sourceCallback)
                Assert.Contains(Flatten(disposeError), error => ReferenceEquals(error, callbackFailure));
            if (failCleanup)
                Assert.Contains(Flatten(disposeError), error => ReferenceEquals(error, stage.Failure));
            Assert.Same(completionError, await Record.ExceptionAsync(async () => await run.Completion));
            Assert.False(stage.DisposedWhileActive);
            Assert.Equal(1, stage.DisposeCount);
        }
        finally
        {
            stage.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await run.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraceWaitCancellationWinningBeforeExecutionCompletion_RetainsLateFailure(bool structuredFailure)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("execution completed during cancellation promise cleanup");
        var execution = new TaskCompletionSource<TypedStageExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new CancellationRaceTimeProvider(cancellation, () =>
        {
            if (structuredFailure)
                execution.TrySetResult(new TypedStageExecutionResult(false, null,
                    new SmartPipeError(failure.Message, ErrorType.Permanent, "Late", failure),
                    StageResultKind.Failure, 42, 1, null, false));
            else
                execution.TrySetException(failure);
        });
        var result = await TypedPipelineExecutor<int, int>.TryWaitForCooperativeTimeoutCompletionAsync(
            new PipelineTime(new TimeProviderPipelineClock(time)), execution.Task, TimeSpan.FromMinutes(3), cancellation.Token);
        Assert.Equal(TypedPipelineExecutor<int, int>.TimedAttemptCompletionKind.CallerCancelled, result.Kind);
        Assert.True(execution.Task.IsCompleted);
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        registry.Register("stage", "Stage", 42, 1, execution.Task, cancellation, Timeout.InfiniteTimeSpan,
            reportUnexpectedFaults: true, getResultError: () => execution.Task.Result.Error?.InnerException);
        Assert.Same(failure, Assert.Single(await registry.WaitForAllAsync()));
    }

    [Theory]
    [InlineData("AttemptTimeout", -2L)]
    [InlineData("StageTimeout", -2L)]
    [InlineData("CancellationGracePeriod", -2L)]
    [InlineData("LateAttemptFinalizationTimeout", -2L)]
    [InlineData("AttemptTimeout", 4294967295L)]
    [InlineData("StageTimeout", 4294967295L)]
    [InlineData("CancellationGracePeriod", 4294967295L)]
    [InlineData("LateAttemptFinalizationTimeout", 4294967295L)]
    [InlineData("RetryMode", 99L)]
    public void InvalidTimeoutPolicy_FailsBeforeAnyFactoryActivation(string property, long value)
    {
        var activations = 0;
        var duration = TimeSpan.FromMilliseconds(value);
        var policy = new TimeoutPolicy
        {
            AttemptTimeout = property == "AttemptTimeout" ? duration : null,
            StageTimeout = property == "StageTimeout" ? duration : null,
            CancellationGracePeriod = property == "CancellationGracePeriod" ? duration : TimeSpan.Zero,
            LateAttemptFinalizationTimeout = property == "LateAttemptFinalizationTimeout" ? duration : TimeSpan.Zero,
            RetryMode = property == "RetryMode" ? (TimeoutRetryMode)value : TimeoutRetryMode.CooperativeOnly,
        };
        var builder = PipelineDefinitionBuilder.From(new PipelineKey("timeout-validation"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            {
                activations++;
                return ValueTask.FromResult<IPipelineSource<int>>(new EnumerablePipelineSource<int>([1]));
            }));
        var transformer = PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
        {
            activations++;
            return ValueTask.FromResult<IPipelineTransformer<int, int>>(new HeldTransformer(false));
        });
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => builder
            .Transform(new PipelineStageKey("transform"), transformer, new StageFailureOptions { Timeout = policy }).Build());
        Assert.Equal(property, error.ParamName);
        Assert.Equal(0, activations);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4294967294L)]
    public void SupportedTimeoutBoundaries_AreAccepted(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var snapshot = StageFailureOptionsSnapshot.Create(new StageFailureOptions
        {
            Timeout = new TimeoutPolicy
            {
                AttemptTimeout = duration,
                StageTimeout = duration,
                CancellationGracePeriod = duration,
                LateAttemptFinalizationTimeout = duration,
            },
        });
        snapshot.Validate();
        Assert.Equal(duration, snapshot.Timeout!.AttemptTimeout);
    }

    private static PipelineRun<int> CreateRun(HeldTransformer stage, ObservedTimeProvider time, TimeSpan grace, int concurrency = 1) =>
        PipelineBuilder.From(new EnumerablePipelineSource<int>([1])).Transform(stage, new StageFailureOptions
        {
            Timeout = new TimeoutPolicy
            {
                AttemptTimeout = AttemptTimeout,
                CancellationGracePeriod = grace,
                LateAttemptFinalizationTimeout = FinalizationTimeout,
            },
        }).WithRuntimeOptions(new PipelineRuntimeOptions { Clock = new TimeProviderPipelineClock(time), MaxConcurrency = concurrency }).Run();

    private static IEnumerable<Exception> Flatten(Exception? error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten),
        PipelineFailureActionException failure => new[] { failure }.Concat(Flatten(failure.Error.InnerException)),
        null => [],
        _ => new[] { error }.Concat(Flatten(error.InnerException)),
    };

    private sealed class ThrowingCancellationSource(Exception failure) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            using var registration = ct.Register(() => throw failure);
            yield return ProcessingEnvelope<int>.Create(1);
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationRaceTimeProvider(CancellationTokenSource cancellation, Action completeExecution) : FakeTimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // Cancellation wins the promise; disposing its timer completes the
            // underlying execution before WaitAsync returns to the catch filter.
            cancellation.Cancel();
            return new CompletionOnDisposeTimer(timer, completeExecution);
        }
    }

    private sealed class CompletionOnDisposeTimer(ITimer timer, Action completeExecution) : ITimer
    {
        private int _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            timer.Dispose();
            completeExecution();
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EnumerablePipelineSource<T>(IEnumerable<T> values) : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var value in values)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(value);
            }
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldTransformer(bool failCleanup) : IPipelineTransformer<int, int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? CallbackFailure { get; init; }
        public Exception Failure { get; } = new IOException("late cancellation cleanup failed");
        public int DisposeCount { get; private set; }
        public bool DisposedWhileActive { get; private set; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            using var registration = ct.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (CallbackFailure is not null)
                    throw CallbackFailure;
            });
            Entered.TrySetResult();
            try
            {
                await Release.Task;
                if (failCleanup)
                    throw Failure;
                return StageResult<int>.Success(envelope.Payload);
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
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
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
