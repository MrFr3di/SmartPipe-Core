using SmartPipe.Core;
using Microsoft.Extensions.Time.Testing;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class TimedAttemptAdmissionRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Wait_StageDeadlineExpiresWithoutStartingWaitingAttempt()
    {
        var clock = new AdmissionTestTimeProvider();
        var transformer = new HeldTransformer();
        var observer = new CapturingObserver();
        var run = PipelineBuilder.From(new RetryAdmissionRegressionTests.Source(2)).Transform(transformer, new StageFailureOptions
        {
            Timeout = new TimeoutPolicy
            {
                StageTimeout = TimeSpan.FromSeconds(1),
                RetryMode = TimeoutRetryMode.DetachWithoutRetry,
                LateAttemptFinalizationTimeout = Timeout.InfiniteTimeSpan,
            },
        }).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            MaxConcurrency = 2,
            Clock = new TimeProviderPipelineClock(clock),
            TimedAttemptAdmission = new TimedAttemptAdmissionOptions(1, TimedAttemptAdmissionMode.Wait),
        }).WithObserver(observer).Run();
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            await observer.TwoFailures.Task.WaitAsync(Deadline);
            Assert.Equal(1, transformer.Calls);
            Assert.Single(observer.Events.OfType<StageStartedEvent>());
            transformer.Release.TrySetResult();
            await run.Completion.WaitAsync(Deadline);
            Assert.Equal(1, transformer.Calls);
        }
        finally { transformer.Release.TrySetResult(); await run.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalLateAttempt_HoldsCapacityAndDisposeUntilObservation(bool scopeOwned)
    {
        var clock = new AdmissionTestTimeProvider();
        var transformer = new HeldTransformer();
        var observer = new CapturingObserver();
        var component = scopeOwned
            ? PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>((_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(transformer))
            : PipelineComponent.Borrowed<IPipelineTransformer<int, int>>(transformer);
        var definition = PipelineDefinitionBuilder.From(new PipelineKey("admission-late"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new RetryAdmissionRegressionTests.Source(1))))
            .Transform(new PipelineStageKey("held"), component, new StageFailureOptions
            {
                Timeout = new TimeoutPolicy
                {
                    AttemptTimeout = TimeSpan.FromSeconds(1),
                    RetryMode = TimeoutRetryMode.DetachAndRetryIdempotent,
                    LateAttemptFinalizationTimeout = TimeSpan.Zero,
                },
                Retry = new RetryPolicy(1, TimeSpan.FromTicks(1)) { Jitter = RetryJitterMode.Full },
            }).WithRuntimeOptions(new PipelineRuntimeOptions
            {
                Clock = new TimeProviderPipelineClock(clock),
                RetryBudget = new RetryBudgetOptions(1, 0),
                TimedAttemptAdmission = new TimedAttemptAdmissionOptions(1, TimedAttemptAdmissionMode.Reject),
            }).WithObserver(observer).Build();
        var run = await definition.StartAsync(new PipelineActivationContext(definition.Key, Guid.NewGuid(), services: new EmptyServices()));
        try
        {
            await transformer.Entered.Task.WaitAsync(Deadline);
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            var completionError = await Record.ExceptionAsync(() => run.Completion.WaitAsync(Deadline));
            Assert.IsType<TimeoutException>(completionError);
            Assert.Equal(1, transformer.Calls);
            Assert.Single(observer.Events.OfType<StageStartedEvent>());
            Assert.Empty(observer.Events.OfType<RetryAttemptedEvent>());
            Assert.Contains(observer.Events.OfType<StageFailedEvent>(), evt => evt.Error.Category == "TimedAttemptAdmission");
            var disposal = run.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, transformer.Disposals);
            transformer.Release.TrySetResult();
            await disposal.WaitAsync(Deadline);
            Assert.Equal(0, transformer.Disposals);
        }
        finally { transformer.Release.TrySetResult(); await run.DisposeAsync(); }
    }

    [Fact]
    public async Task ActiveAttempt_RejectsBeforeStartWithoutRetryOrBreakerSample()
    {
        var transformer = new HeldTransformer();
        var observer = new CapturingObserver();
        var run = PipelineBuilder.From(new RetryAdmissionRegressionTests.Source(8)).Transform(transformer, new StageFailureOptions
        {
            Timeout = new TimeoutPolicy { AttemptTimeout = TimeSpan.FromMinutes(1) },
            Retry = new RetryPolicy(2, TimeSpan.FromTicks(1)),
            CircuitBreaker = new CircuitBreakerPolicy { FailureThreshold = 1 },
            OnPermanentFailure = FailureAction.EmitFailureResult,
        }).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            MaxConcurrency = 8,
            TimedAttemptAdmission = new TimedAttemptAdmissionOptions(1, TimedAttemptAdmissionMode.Reject),
        }).WithObserver(observer).Run();
        try
        {
            await transformer.Entered.Task.WaitAsync(Deadline);
            await observer.Rejected.Task.WaitAsync(Deadline);
            Assert.Equal(1, transformer.Calls);
            Assert.Single(observer.Events.OfType<StageStartedEvent>());
            Assert.Empty(observer.Events.OfType<RetryAttemptedEvent>());
            Assert.Empty(observer.Events.OfType<CircuitBreakerOpenedEvent>());
            transformer.Release.TrySetResult();
            await run.Completion.WaitAsync(Deadline);
            var outputs = new List<PipelineOutput<int>>();
            while (run.Outputs.TryRead(out var output)) outputs.Add(output);
            Assert.Equal(8, outputs.Count);
            Assert.Equal(7, outputs.Count(output => output.Result.Error?.Category == "TimedAttemptAdmission"));
        }
        finally { transformer.Release.TrySetResult(); await run.DisposeAsync(); }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    internal sealed class HeldTransformer : IPipelineTransformer<int, int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public int Disposals;
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task;
            return StageResult<int>.Success(envelope.Payload);
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }

    private sealed class CapturingObserver : IPipelineObserver
    {
        public System.Collections.Concurrent.ConcurrentQueue<PipelineEvent> Events { get; } = new();
        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoFailures { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rejected;
        private int _failed;
        public ValueTask OnEventAsync(PipelineEvent evt, CancellationToken ct = default)
        {
            Events.Enqueue(evt);
            if (evt is StageFailedEvent && Interlocked.Increment(ref _failed) == 2) TwoFailures.TrySetResult();
            if (evt is StageFailedEvent failed && failed.Error.Category == "TimedAttemptAdmission"
                && Interlocked.Increment(ref _rejected) == 7) Rejected.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class AdmissionTestTimeProvider : FakeTimeProvider
{
    private readonly global::System.Threading.Channels.Channel<TimeSpan> _timers = global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }
    public async Task WaitForTimerAsync(TimeSpan dueTime)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await _timers.Reader.ReadAsync(stop.Token) != dueTime) { }
    }
}
