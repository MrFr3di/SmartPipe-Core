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
        var run = PipelineBuilder.From(new EnumerablePipelineSource<int>([1])).Transform(stage, new StageFailureOptions
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
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EnumerablePipelineSource<int>([1]))))
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

    private static IEnumerable<Exception> Flatten(Exception? error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten),
        PipelineFailureActionException failure => new[] { failure }.Concat(Flatten(failure.Error.InnerException)),
        null => [],
        _ => new[] { error }.Concat(Flatten(error.InnerException)),
    };

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
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (pipelineEvent is not PipelineStartedEvent)
                return;
            using var registration = ct.Register(() => Cancelled.TrySetResult());
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
