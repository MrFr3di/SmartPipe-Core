using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

public sealed class RunRetryBudgetTests
{
    [Fact]
    public void CommitConsumesAndUncommittedDisposeRefundsExactlyOnce()
    {
        var budget = Create(2, 0, out _);
        Assert.True(budget.TryReserve(out var first));
        Assert.True(budget.TryReserve(out var second));
        Assert.False(budget.TryReserve(out _));
        first!.Commit();
        first.Dispose();
        Assert.False(budget.TryReserve(out _));
        second!.Dispose();
        second.Dispose();
        Assert.True(budget.TryReserve(out var refunded));
        refunded!.Commit();
        Assert.False(budget.TryReserve(out _));
    }

    [Fact]
    public void DelayedReservations_DoNotAccumulateRefillBurst()
    {
        var budget = Create(2, 1, out var clock);
        Assert.True(budget.TryReserve(out var first));
        Assert.True(budget.TryReserve(out var second));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(budget.TryReserve(out _));
        first!.Commit();
        second!.Commit();
        Assert.False(budget.TryReserve(out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(budget.TryReserve(out var third));
        Assert.False(budget.TryReserve(out _));
        third!.Dispose();
    }

    [Fact]
    public void FractionalRefillAndRefundAreCappedAndRunsAreIndependent()
    {
        var budget = Create(1, 0.5, out var clock);
        Assert.True(budget.TryReserve(out var first));
        first!.Commit();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(budget.TryReserve(out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(budget.TryReserve(out var second));
        clock.Advance(TimeSpan.FromDays(1));
        second!.Dispose();
        Assert.True(budget.TryReserve(out var third));
        Assert.False(budget.TryReserve(out _));
        Assert.True(new RunRetryBudget(new RetryBudgetOptions(1, 0), new PipelineTime(new TimeProviderPipelineClock(clock))).TryReserve(out var independent));
        third!.Dispose();
        independent!.Dispose();
    }

    [Fact]
    public async Task ConcurrentReservations_HaveExactlyCapacityWinners()
    {
        var budget = Create(7, 0, out _);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 64).Select(async _ =>
        {
            await gate.Task;
            return budget.TryReserve(out var lease) ? lease : null;
        }).ToArray();
        gate.SetResult();
        var leases = (await Task.WhenAll(tasks)).OfType<RetryBudgetLease>().ToArray();
        Assert.Equal(7, leases.Length);
        await Task.WhenAll(leases.Select(lease => Task.Run(() => { lease.Commit(); lease.Dispose(); })));
        Assert.False(budget.TryReserve(out _));
    }

    private static RunRetryBudget Create(int capacity, double refill, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider();
        return new RunRetryBudget(new RetryBudgetOptions(capacity, refill), new PipelineTime(new TimeProviderPipelineClock(clock)));
    }
}
