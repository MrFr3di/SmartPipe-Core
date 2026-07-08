#nullable enable

using FluentAssertions;
using SmartPipe.Core;
using System.Runtime.ExceptionServices;

namespace SmartPipe.Core.Tests.Runtime.Execution;

[Trait("Category", "CorrectnessRegression")]
public sealed class PipelineLifecycleControllerTests
{
    [Fact]
    public void InitialState_ShouldBeNotStarted()
    {
        var controller = new PipelineLifecycleController();

        controller.State.Should().Be(PipelineRunState.NotStarted);
    }

    [Fact]
    public void MarkRunning_ShouldTransitionOnlyFromNotStartedToRunning()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.State.Should().Be(PipelineRunState.Running);

        controller.MarkCompleted();
        controller.MarkRunning();
        controller.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public void MarkDrainingIfRunning_ShouldTransitionOnlyFromRunningToDraining()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkDrainingIfRunning();
        controller.State.Should().NotBe(PipelineRunState.Draining);

        controller.MarkRunning();
        controller.MarkDrainingIfRunning();

        controller.State.Should().Be(PipelineRunState.Draining);
    }

    [Fact]
    public void MarkCompleted_ShouldNotOverwriteTerminalStates()
    {
        var completed = new PipelineLifecycleController();
        completed.MarkRunning();
        completed.MarkCompleted();
        completed.MarkCompleted();
        completed.State.Should().Be(PipelineRunState.Completed);

        var cancelled = new PipelineLifecycleController();
        cancelled.MarkCancelled();
        cancelled.MarkCompleted();
        cancelled.State.Should().Be(PipelineRunState.Cancelled);

        var aborted = new PipelineLifecycleController();
        aborted.MarkAborted();
        aborted.MarkCompleted();
        aborted.State.Should().Be(PipelineRunState.Aborted);

        var faulted = new PipelineLifecycleController();
        faulted.MarkFaulted();
        faulted.MarkCompleted();
        faulted.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public void MarkCompletedIfDraining_ShouldTransitionOnlyFromDrainingToCompleted()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkCompletedIfDraining();
        controller.State.Should().NotBe(PipelineRunState.Completed);

        controller.MarkRunning();
        controller.MarkCompletedIfDraining();
        controller.State.Should().Be(PipelineRunState.Running);

        controller.MarkDrainingIfRunning();
        controller.MarkCompletedIfDraining();

        controller.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public void MarkCompletedIfDraining_ShouldNotOverwriteAborted()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkDrainingIfRunning();
        controller.MarkAborted();
        controller.MarkCompletedIfDraining();

        controller.State.Should().Be(PipelineRunState.Aborted);
    }

    [Fact]
    public void MarkCancelledUnlessAborted_ShouldNotOverwriteAborted()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkAborted();
        controller.MarkCancelledUnlessAborted();

        controller.State.Should().Be(PipelineRunState.Aborted);
    }

    [Fact]
    public void MarkCancelledUnlessAborted_ShouldNotOverwriteCompleted()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkDrainingIfRunning();
        controller.MarkCompletedIfDraining();
        controller.MarkCancelledUnlessAborted();

        controller.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public void MarkCancelledUnlessAborted_ShouldNotOverwriteFaulted()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkFaulted();
        controller.MarkCancelledUnlessAborted();

        controller.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public void MarkCancelledUnlessAborted_IsIdempotentWhenAlreadyCancelled()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkCancelled();
        controller.MarkCancelledUnlessAborted();

        controller.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public void MarkCancelledUnlessAborted_TransitionsFromRunningToCancelled()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkCancelledUnlessAborted();

        controller.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public void MarkCancelledUnlessAborted_TransitionsFromNotStartedToCancelled()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkCancelledUnlessAborted();

        controller.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public void MarkCompleted_TransitionsFromRunningToCompleted()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkCompleted();

        controller.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public void MarkCompleted_FromNotStarted_IsNoOp()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkCompleted();

        controller.State.Should().Be(PipelineRunState.NotStarted);
    }

    [Fact]
    public void MarkTerminal_ShouldNotOverwritePreviouslyPublishedTerminalState()
    {
        var controller = new PipelineLifecycleController();

        controller.MarkRunning();
        controller.MarkAborted();
        controller.MarkTerminal(PipelineRunState.Faulted);

        controller.State.Should().Be(PipelineRunState.Aborted);
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ConcurrentTerminalTransitions_EndInTerminalState()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var controller = new PipelineLifecycleController();
            controller.MarkRunning();
            controller.MarkDrainingIfRunning();

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = new[]
            {
                Task.Run(async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    controller.MarkCompletedIfDraining();
                }),
                Task.Run(async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    controller.MarkCancelledUnlessAborted();
                }),
                Task.Run(async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    controller.MarkAborted();
                }),
            };

            gate.SetResult();

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

            controller.State.Should().BeOneOf(
                PipelineRunState.Completed,
                PipelineRunState.Cancelled,
                PipelineRunState.Aborted);
        }
    }

    [Fact]
    public async Task RuntimeCleanup_CollectAsync_ReturnsAllErrorsWithoutThrowing()
    {
        var first = new InvalidOperationException("first cleanup");
        var second = new ApplicationException("second cleanup");

        var errors = await RuntimeCleanup.CollectAsync([
            () => ValueTask.CompletedTask,
            () => throw first,
            async () =>
            {
                await Task.Yield();
                throw second;
            },
        ]);

        errors.Should().Equal(first, second);
    }

    [Fact]
    public void RuntimeCleanup_ThrowCombined_RethrowsPrimaryWhenAlone()
    {
        var primary = new InvalidOperationException("primary");

        var act = () => RuntimeCleanup.ThrowCombined(ExceptionDispatchInfo.Capture(primary), []);

        act.Should().Throw<InvalidOperationException>()
            .Which.Should().BeSameAs(primary);
    }

    [Fact]
    public void RuntimeCleanup_ThrowCombined_RethrowsSingleCleanupOnlyError()
    {
        var cleanup = new InvalidOperationException("cleanup");

        var act = () => RuntimeCleanup.ThrowCombined(null, [cleanup]);

        act.Should().Throw<InvalidOperationException>()
            .Which.Should().BeSameAs(cleanup);
    }

    [Fact]
    public void RuntimeCleanup_ThrowCombined_AggregatesPrimaryFirstWhenMultipleErrorsExist()
    {
        var primary = new InvalidOperationException("primary");
        var cleanup = new ApplicationException("cleanup");

        var act = () => RuntimeCleanup.ThrowCombined(ExceptionDispatchInfo.Capture(primary), [cleanup]);

        var aggregate = act.Should().Throw<AggregateException>().Which;
        aggregate.InnerExceptions.Should().Equal(primary, cleanup);
    }
}
