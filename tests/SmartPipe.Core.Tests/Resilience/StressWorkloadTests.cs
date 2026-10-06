namespace SmartPipe.Core.Tests.Resilience;

public sealed class StressWorkloadTests
{
    [Fact(Timeout = 10000)]
    public async Task Deadline_FailsInsteadOfAcceptingPartialWorkAndDrainsWorkers()
    {
        using var workers = new CancellationTokenSource();
        var terminated = false;
        var work = WorkerAsync();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => StressWorkload.WaitForCompletionAsync(
                work, workers, TimeSpan.Zero, TestContext.Current.CancellationToken));
            Assert.True(terminated);
        }
        finally
        {
            await workers.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        }

        async Task WorkerAsync()
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, workers.Token); }
            finally { terminated = true; }
        }
    }

    [Fact(Timeout = 10000)]
    public async Task CallerCancellation_IsPropagatedAfterWorkerCleanup()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        var terminated = false;
        var work = WorkerAsync();
        var wait = StressWorkload.WaitForCompletionAsync(work, workers, TimeSpan.FromMinutes(1), caller.Token);
        caller.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.True(terminated);
        }
        finally
        {
            await workers.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        }

        async Task WorkerAsync()
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, workers.Token); }
            finally { terminated = true; }
        }
    }

    [Fact]
    public async Task WorkerFailure_IsObservedAndPropagated()
    {
        using var workers = new CancellationTokenSource();
        var original = new InvalidOperationException("worker failed");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StressWorkload.WaitForCompletionAsync(
            Task.FromException(original), workers, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Same(original, error);
    }

    [Fact(Timeout = 20000)]
    public async Task NonCooperativeWorker_PreservesCallerCancellationAfterCleanupDeadline()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var workers = new CancellationTokenSource();
        var work = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        caller.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StressWorkload.WaitForCompletionAsync(
                work.Task, workers, TimeSpan.FromMinutes(1), caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.True(workers.IsCancellationRequested);
            Assert.False(work.Task.IsCompleted);
        }
        finally
        {
            work.TrySetResult(null);
        }
    }

    [Fact]
    public async Task WorkerFailure_IsPreservedWhenCancellationCallbackThrows()
    {
        using var workers = new CancellationTokenSource();
        using var registration = workers.Token.Register(() => throw new InvalidOperationException("cleanup callback failed"));
        var original = new InvalidOperationException("worker failed");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StressWorkload.WaitForCompletionAsync(
            Task.FromException(original), workers, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Same(original, error);
    }

    [Fact(Timeout = 20000)]
    public async Task NonCooperativeCancellationCallback_PreservesWorkerFailureWithinCleanupDeadline()
    {
        using var workers = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        using var registration = workers.Token.Register(() => release.Wait());
        var original = new InvalidOperationException("worker failed");
        var wait = StressWorkload.WaitForCompletionAsync(
            Task.FromException(original), workers, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(original, error);
        }
        finally
        {
            release.Set();
            await Record.ExceptionAsync(() => wait);
        }
    }

    [Fact]
    public async Task CompletedWork_DoesNotCancelWorkers()
    {
        using var workers = new CancellationTokenSource();
        await StressWorkload.WaitForCompletionAsync(
            Task.CompletedTask, workers, TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.False(workers.IsCancellationRequested);
    }
}
