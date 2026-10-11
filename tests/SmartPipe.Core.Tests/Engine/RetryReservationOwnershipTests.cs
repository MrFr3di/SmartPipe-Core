using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
public sealed class RetryReservationOwnershipTests
{
    [Theory]
    [InlineData("StageFailed")]
    [InlineData("RetryScheduled")]
    [InlineData("OnRetry")]
    [InlineData("StageStarted")]
    public async Task PreStartFault_RefundsReservation(string site)
    {
        var clock = new AdmissionTestTimeProvider();
        var budget = new RunRetryBudget(new RetryBudgetOptions(1, 0), new PipelineTime(new TimeProviderPipelineClock(clock)));
        var transformer = new RetryAdmissionRegressionTests.FailingTransformer();
        var error = new IOException(site);
        var callbacks = 0;
        var stage = new TypedPipelineStage<int, int>(transformer, 0, new StageFailureOptions
        {
            Retry = new RetryPolicy(1, TimeSpan.FromTicks(1), onRetry: (_, _, _) =>
            {
                callbacks++;
                if (site == "OnRetry") throw error;
            }),
        });
        var executor = Create(budget, clock, (evt, _) =>
        {
            if (site == "StageFailed" && evt is StageFailedEvent
                || site == "StageStarted" && evt is StageStartedEvent started && started.Attempt == 1)
                throw error;
            return ValueTask.CompletedTask;
        }, (_, _, _, _, _, _) => site == "RetryScheduled" ? throw error : ValueTask.CompletedTask);
        var actual = await Record.ExceptionAsync(() => executor.ExecuteAsync(stage, ProcessingEnvelope<int>.Create(1), default).AsTask());
        Assert.Same(error, actual);
        Assert.Equal(1, transformer.Calls);
        Assert.Equal(site is "OnRetry" or "StageStarted" ? 1 : 0, callbacks);
        Assert.True(budget.TryReserve(out var refunded));
        refunded!.Dispose();
    }

    [Fact]
    public async Task CancelledDelay_RefundsWithoutCallback()
    {
        var clock = new AdmissionTestTimeProvider();
        var budget = new RunRetryBudget(new RetryBudgetOptions(1, 0), new PipelineTime(new TimeProviderPipelineClock(clock)));
        var transformer = new RetryAdmissionRegressionTests.FailingTransformer();
        var callbacks = 0;
        var stage = new TypedPipelineStage<int, int>(transformer, 0, new StageFailureOptions
        {
            Retry = new RetryPolicy(1, onRetry: (_, _, _) => callbacks++),
        });
        using var cancellation = new CancellationTokenSource();
        var executor = Create(budget, clock, (_, _) => ValueTask.CompletedTask,
            (_, _, _, _, _, _) => ValueTask.CompletedTask, TimeSpan.FromSeconds(1));
        var execution = executor.ExecuteAsync(stage, ProcessingEnvelope<int>.Create(1), cancellation.Token).AsTask();
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        Assert.False(budget.TryReserve(out _));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, transformer.Calls);
        Assert.True(budget.TryReserve(out var refunded));
        refunded!.Dispose();
    }

    private static StageExecutor Create(RunRetryBudget budget, AdmissionTestTimeProvider clock,
        Func<PipelineEvent, CancellationToken, ValueTask> emit,
        Func<ITypedPipelineStage, TypedStageExecutionResult, int, TimeSpan, SmartPipeError, CancellationToken, ValueTask> scheduled,
        TimeSpan? delay = null)
    {
        var pipelineClock = new TimeProviderPipelineClock(clock);
        return new StageExecutor("budget", "run", LineageMode.Off, pipelineClock, new PipelineTime(pipelineClock), _ => null,
            (_, _, attempt, _) => new RetryDecision(attempt == 0 ? RetryDecisionKind.Retry : RetryDecisionKind.Exhausted, 1, delay ?? TimeSpan.Zero),
            scheduled, (_, _, _, _) => ValueTask.CompletedTask, (_, _, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask, emit,
            (stage, current, _, ct) => stage.ExecuteAsync(current, LineageMode.Off, pipelineClock, ct),
            retryBudget: budget,
            executeAdmittedAttemptAsync: (stage, current, _, ct, admission) =>
            {
                admission!.MarkExecutionStarted();
                return stage.ExecuteAsync(current, LineageMode.Off, pipelineClock, ct);
            });
    }
}
