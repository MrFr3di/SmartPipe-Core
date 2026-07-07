#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class LateStageAttemptRegistryTests
{
    [Fact]
    public void RegisterAfterSeal_ShouldRejectAttemptAndDisposeTimeoutCts()
    {
        var registry = new LateStageAttemptRegistry(new PipelineTime(SystemPipelineClock.Instance));
        using var attemptCompletion = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        registry.Seal();

        var accepted = registry.Register(
            "stage-1",
            "StageOne",
            traceId: 42,
            attempt: 1,
            execution.Task,
            attemptCompletion,
            TimeSpan.FromSeconds(5));

        accepted.Should().BeFalse();
        var act = () => _ = attemptCompletion.Token;
        act.Should().Throw<ObjectDisposedException>();
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
            TimeSpan.FromSeconds(5)).Should().BeTrue();

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
            TimeSpan.FromMilliseconds(1)).Should().BeTrue();

        var errors = await registry.WaitForAllAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        errors.Should().ContainSingle()
            .Which.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain("stage-1#1");

        execution.SetResult();
        await registry.WaitForStageAttemptsToCompleteAsync("stage-1").WaitAsync(TimeSpan.FromSeconds(5));
    }
}
