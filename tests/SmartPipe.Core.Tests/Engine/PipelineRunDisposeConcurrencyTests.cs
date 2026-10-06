using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class PipelineRunDisposeConcurrencyTests
{
    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task DisposeAsync_64ConcurrentCallersShareOneCleanupTask()
    {
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var run = CreateRun(async () =>
        {
            Interlocked.Increment(ref callbackCount);
            callbackEntered.TrySetResult(null);
            await release.Task.ConfigureAwait(false);
        });

        var disposals = Enumerable.Range(0, 64)
            .Select(_ => run.DisposeAsync())
            .ToArray();
        var tasks = disposals.Select(dispose => dispose.AsTask()).ToArray();

        await callbackEntered.Task;
        tasks.Select(task => task).Should().OnlyContain(task => ReferenceEquals(task, tasks[0]));
        tasks[0].IsCompleted.Should().BeFalse();

        release.TrySetResult(null);
        await Task.WhenAll(tasks);

        callbackCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task DisposeAsync_SynchronousThrowIsCachedForAllCallers()
    {
        var expected = new InvalidOperationException("dispose failed");
        var callbackCount = 0;

        ValueTask Dispose()
        {
            Interlocked.Increment(ref callbackCount);
            throw expected;
        }

        var run = CreateRun(Dispose);
        var tasks = Enumerable.Range(0, 64)
            .Select(_ => run.DisposeAsync().AsTask())
            .ToArray();

        var errors = await Task.WhenAll(tasks.Select(async task =>
            await Record.ExceptionAsync(() => task)));

        errors.Should().OnlyContain(error => ReferenceEquals(error, expected));
        callbackCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task DisposeAsync_DoesNotHoldLockWhileCallingCleanup()
    {
        PipelineRun<int>? run = null;
        Task? nested = null;
        run = CreateRun(() =>
        {
            nested = run!.DisposeAsync().AsTask();
            return ValueTask.CompletedTask;
        });

        var current = run;
        var dispose = Task.Run(() => current!.DisposeAsync().AsTask());

        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        nested.Should().NotBeNull();
        await nested!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PipelineRun<int> CreateRun(Func<ValueTask> dispose) =>
        new(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            Task.CompletedTask,
            () => PipelineRunState.Completed,
            dispose: dispose);
}
