#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

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
    public async Task MarkAborted_WhenRacingWithCompletedAndCancelled_ShouldWinEventually()
    {
        for (var iteration = 0; iteration < 1_000; iteration++)
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

            controller.MarkAborted();
            controller.State.Should().Be(PipelineRunState.Aborted);
        }
    }
}
