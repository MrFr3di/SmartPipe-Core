#nullable enable

using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class PipelineRunWithLifetimeTests
{
    [Fact]
    public void WithLifetime_NullCompletion_ThrowsArgumentNullException()
    {
        var run = CreateRun();

        var act = () => run.WithLifetime(null!, () => ValueTask.CompletedTask);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("completion");
    }

    [Fact]
    public void WithLifetime_NullDispose_ThrowsArgumentNullException()
    {
        var run = CreateRun();

        var act = () => run.WithLifetime(Task.CompletedTask, null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("dispose");
    }

    [Fact]
    public void WithLifetime_ReturnsNewRunWithReplacementCompletion()
    {
        var run = CreateRun();
        var replacementCompletion = new TaskCompletionSource().Task;

        var derived = run.WithLifetime(replacementCompletion, () => ValueTask.CompletedTask);

        derived.Completion.Should().BeSameAs(replacementCompletion);
        derived.Completion.Should().NotBeSameAs(run.Completion);
    }

    [Fact]
    public async Task WithLifetime_ReplacementDisposeIsInvoked()
    {
        var run = CreateRun();
        var disposeCalled = false;

        var derived = run.WithLifetime(Task.CompletedTask, () =>
        {
            disposeCalled = true;
            return ValueTask.CompletedTask;
        });

        await derived.DisposeAsync();

        disposeCalled.Should().BeTrue();
    }

    [Fact]
    public async Task WithLifetime_PreservesOutputs()
    {
        var channel = Channel.CreateUnbounded<PipelineOutput<int>>();
        var run = CreateRunWithOutputs(channel.Reader);

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);

        derived.Outputs.Should().BeSameAs(run.Outputs);
        derived.Outputs.Should().BeSameAs(channel.Reader);

        channel.Writer.Complete();
        var items = new List<PipelineOutput<int>>();
        await foreach (var item in derived.Outputs.ReadAllAsync())
            items.Add(item);

        items.Should().BeEmpty();
    }

    [Fact]
    public void WithLifetime_PreservesStateDelegate()
    {
        var state = PipelineRunState.Running;
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => state);

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);

        derived.State.Should().Be(PipelineRunState.Running);

        state = PipelineRunState.Completed;
        derived.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task WithLifetime_PreservesCancelDelegate()
    {
        var cancelCalled = false;
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Running,
            cancel: _ =>
            {
                cancelCalled = true;
                return ValueTask.CompletedTask;
            });

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);
        await derived.CancelAsync();

        cancelCalled.Should().BeTrue();
    }

    [Fact]
    public async Task WithLifetime_PreservesDrainDelegate()
    {
        var drainCalled = false;
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Running,
            drain: (_, _) =>
            {
                drainCalled = true;
                return ValueTask.CompletedTask;
            });

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);
        await derived.DrainAsync(TimeSpan.FromSeconds(1));

        drainCalled.Should().BeTrue();
    }

    [Fact]
    public async Task WithLifetime_PreservesTryDrainDelegate()
    {
        var tryDrainCalled = false;
        var expected = new PipelineDrainResult(
            PipelineDrainStatus.Completed,
            PipelineRunState.Completed,
            TimeSpan.FromMilliseconds(12));
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Running,
            cancel: null,
            drain: null,
            tryDrain: (_, _) =>
            {
                tryDrainCalled = true;
                return ValueTask.FromResult(expected);
            },
            abort: null,
            dispose: null,
            metricsProvider: null);

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);
        var actual = await derived.TryDrainAsync(TimeSpan.FromSeconds(1));

        tryDrainCalled.Should().BeTrue();
        actual.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task WithLifetime_PreservesAbortDelegate()
    {
        var abortCalled = false;
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Running,
            abort: _ =>
            {
                abortCalled = true;
                return ValueTask.CompletedTask;
            });

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);
        await derived.AbortAsync();

        abortCalled.Should().BeTrue();
    }

    [Fact]
    public void WithLifetime_PreservesMetricsDelegate()
    {
        var snapshot = new SmartPipeMetricsRecorder();
        snapshot.RecordProcessed(2.5);
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Completed,
            cancel: null,
            drain: null,
            tryDrain: null,
            abort: null,
            dispose: null,
            metricsProvider: snapshot.CaptureSnapshot);

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);

        derived.Metrics.ItemsProcessed.Should().Be(1);
    }

    [Fact]
    public void WithLifetime_OriginalRunIsNotMutated()
    {
        var originalCompletion = new TaskCompletionSource().Task;
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: originalCompletion,
            stateProvider: () => PipelineRunState.Completed);

        var replacementCompletion = new TaskCompletionSource().Task;
        _ = run.WithLifetime(replacementCompletion, () => ValueTask.CompletedTask);

        run.Completion.Should().BeSameAs(originalCompletion);
    }

    [Fact]
    public async Task WithLifetime_OriginalDisposeIsNotInvokedWhenDerivedIsDisposed()
    {
        var originalDisposeCalled = false;
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Completed,
            dispose: () =>
            {
                originalDisposeCalled = true;
                return ValueTask.CompletedTask;
            });

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);
        await derived.DisposeAsync();

        originalDisposeCalled.Should().BeFalse();
    }

    [Fact]
    public async Task WithLifetime_ReplacementCompletionCanFault()
    {
        var run = CreateRun();
        var expected = new InvalidOperationException("wrapped fault");
        var completion = Task.FromException(expected);

        var derived = run.WithLifetime(completion, () => ValueTask.CompletedTask);

        var ex = await Record.ExceptionAsync(() => derived.Completion);

        ex.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task WithLifetime_PreservesNullDelegatesAsNoOps()
    {
        var run = new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Completed);

        var derived = run.WithLifetime(Task.CompletedTask, () => ValueTask.CompletedTask);

        var cancelAct = async () => await derived.CancelAsync();
        var drainAct = async () => await derived.DrainAsync(TimeSpan.FromSeconds(1));
        var abortAct = async () => await derived.AbortAsync();

        await cancelAct.Should().NotThrowAsync();
        await drainAct.Should().NotThrowAsync();
        await abortAct.Should().NotThrowAsync();
    }

    private static PipelineRun<int> CreateRun()
    {
        return new PipelineRun<int>(
            outputs: Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Completed);
    }

    private static PipelineRun<int> CreateRunWithOutputs(ChannelReader<PipelineOutput<int>> reader)
    {
        return new PipelineRun<int>(
            outputs: reader,
            completion: Task.CompletedTask,
            stateProvider: () => PipelineRunState.Completed);
    }
}
