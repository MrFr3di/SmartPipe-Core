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

    [Fact]
    public async Task CompletedWork_DoesNotCancelWorkers()
    {
        using var workers = new CancellationTokenSource();
        await StressWorkload.WaitForCompletionAsync(
            Task.CompletedTask, workers, TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.False(workers.IsCancellationRequested);
    }
}
