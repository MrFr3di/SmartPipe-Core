#nullable enable

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class LateStageAttemptRegistryTests
{
    [Fact]
    public async Task RegisterAfterSeal_ShouldFaultClosedObserveAttemptAndDisposeTimeoutCts()
    {
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        using var attemptCompletion = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Seal();

        var act = () => registry.Register(
                "stage-1",
                "StageOne",
                traceId: 42,
                attempt: 1,
                execution.Task,
                attemptCompletion,
                TimeSpan.FromSeconds(5));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Late stage attempt registration occurred after registry sealing.");

        execution.SetException(new InvalidOperationException("late attempt boom"));
        await EventuallyAsync(() =>
        {
            var token = () => _ = attemptCompletion.Token;
            token.Should().Throw<ObjectDisposedException>();
        });
    }

    [Fact]
    public async Task HasRunningAttempt_ShouldRemainTrueUntilAttemptCompletes()
    {
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        using var timeoutCts = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Register(
            "stage-1",
            "StageOne",
            traceId: 42,
            attempt: 1,
            execution.Task,
            timeoutCts,
            TimeSpan.FromSeconds(5));

        registry.HasRunningAttempt("stage-1").Should().BeTrue();

        execution.SetResult();
        await registry.WaitForStageAttemptsToCompleteAsync("stage-1").WaitAsync(TimeSpan.FromSeconds(5));

        registry.HasRunningAttempt("stage-1").Should().BeFalse();
    }

    [Fact]
    public async Task WaitForAllAsync_ShouldReturnLateAttemptTimeoutErrors()
    {
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        using var timeoutCts = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Register(
            "stage-1",
            "StageOne",
            traceId: 42,
            attempt: 1,
            execution.Task,
            timeoutCts,
            TimeSpan.FromMilliseconds(1));

        var errors = await registry.WaitForAllAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        errors.Should().ContainSingle()
            .Which.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain("stage-1#1");

        execution.SetResult();
        await registry.WaitForStageAttemptsToCompleteAsync("stage-1").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForStageAttemptsToCompleteAsync_ShouldOnlyWaitForRequestedStage()
    {
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Register("stage-1", "StageOne", 42, 1, first.Task, firstCts, TimeSpan.FromSeconds(5));
        registry.Register("stage-2", "StageTwo", 42, 1, second.Task, secondCts, TimeSpan.FromSeconds(5));

        var wait = registry.WaitForStageAttemptsToCompleteAsync("stage-1");
        second.SetResult();

        await Task.Yield();
        wait.IsCompleted.Should().BeFalse();

        first.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SealRegisterRace_ShouldNeverAcceptRegistrationAfterSeal()
    {
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        var start = new ManualResetEventSlim();
        var sealedCount = 0;
        var faultedCount = 0;

        var sealer = Task.Run(() =>
        {
            start.Wait();
            registry.Seal();
            Interlocked.Exchange(ref sealedCount, 1);
        });

        var register = Task.Run(() =>
        {
            start.Wait();
            using var cts = new CancellationTokenSource();
            var execution = Task.CompletedTask;
            try
            {
                registry.Register(
                    "stage-1",
                    "StageOne",
                    traceId: 42,
                    attempt: 1,
                    execution,
                    cts,
                    TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref faultedCount);
            }
        });

        start.Set();
        await Task.WhenAll(sealer, register).WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref sealedCount).Should().Be(1);

        if (Volatile.Read(ref faultedCount) == 0)
        {
            var act = () => registry.Register(
                "stage-2",
                "StageTwo",
                traceId: 42,
                attempt: 1,
                Task.CompletedTask,
                new CancellationTokenSource(),
                TimeSpan.FromSeconds(5));
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task WaitForAllAsync_ShouldUseProviderBackedFinalizationTimeout()
    {
        var timeProvider = new ObservedFakeTimeProvider();
        var registry = new LateStageAttemptRegistry(
            new PipelineTime(new TimeProviderPipelineClock(timeProvider)));
        using var timeoutCts = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromMinutes(10);

        registry.Register(
            "stage-1",
            "StageOne",
            traceId: 42,
            attempt: 1,
            execution.Task,
            timeoutCts,
            timeout);

        var wait = registry.WaitForAllAsync().AsTask();
        await timeProvider.WaitForTimerRegistrationAsync(timeout, Timeout.InfiniteTimeSpan);

        wait.IsCompleted.Should().BeFalse();

        timeProvider.Advance(timeout);

        var errors = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        errors.Should().ContainSingle()
            .Which.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain("stage-1#1");

        execution.SetResult();
    }

    private static async Task EventuallyAsync(Action assertion)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch when (!cts.IsCancellationRequested)
            {
                await Task.Yield();
            }
        }
    }

    private sealed class ObservedFakeTimeProvider : FakeTimeProvider
    {
        private readonly object _gate = new();
        private readonly List<TimerRegistration> _registrations = [];
        private readonly List<TimerWaiter> _waiters = [];

        public Task WaitForTimerRegistrationAsync(
            TimeSpan dueTime,
            TimeSpan period,
            int expectedCount = 1)
        {
            lock (_gate)
            {
                var actual = CountRegistrations(dueTime, period);
                if (actual >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TimerWaiter(
                    dueTime,
                    period,
                    expectedCount,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                _waiters.Add(waiter);
                return waiter.Completion.Task;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            lock (_gate)
            {
                _registrations.Add(new TimerRegistration(dueTime, period));
                CompleteSatisfiedWaiters();
            }

            return timer;
        }

        private int CountRegistrations(TimeSpan dueTime, TimeSpan period) =>
            _registrations.Count(registration =>
                registration.DueTime == dueTime && registration.Period == period);

        private void CompleteSatisfiedWaiters()
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (CountRegistrations(waiter.DueTime, waiter.Period) < waiter.ExpectedCount)
                    continue;

                _waiters.RemoveAt(i);
                waiter.Completion.TrySetResult();
            }
        }

        private readonly record struct TimerRegistration(TimeSpan DueTime, TimeSpan Period);

        private sealed record TimerWaiter(
            TimeSpan DueTime,
            TimeSpan Period,
            int ExpectedCount,
            TaskCompletionSource Completion);
    }
}
