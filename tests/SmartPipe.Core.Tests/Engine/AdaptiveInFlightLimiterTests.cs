#nullable enable

using FluentAssertions;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveInFlightLimiterTests
{
    [Fact]
    public async Task AcquireAsync_ShouldRespectCurrentLimit()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        await using var lease = await limiter.AcquireAsync(CancellationToken.None);

        var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();

        pending.IsCompleted.Should().BeFalse();

        await lease.DisposeAsync();

        var secondLease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task ReleasingLease_ShouldWakeExactlyOneWaiterWhenCapacityIsAvailable()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        await using var lease = await limiter.AcquireAsync(CancellationToken.None);

        var first = limiter.AcquireAsync(CancellationToken.None).AsTask();
        var second = limiter.AcquireAsync(CancellationToken.None).AsTask();

        await lease.DisposeAsync();
        var firstLease = await first.WaitAsync(TimeSpan.FromSeconds(5));

        second.IsCompleted.Should().BeFalse();

        await firstLease.DisposeAsync();
        var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task ShrinkingLimit_ShouldNotRevokeActiveLeases()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 2);
        var first = await limiter.AcquireAsync(CancellationToken.None);
        var second = await limiter.AcquireAsync(CancellationToken.None);

        limiter.UpdateLimit(1);
        var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();

        await first.DisposeAsync();
        pending.IsCompleted.Should().BeFalse("one active lease remains and the new limit is one");

        await second.DisposeAsync();
        var third = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await third.DisposeAsync();
    }

    [Fact]
    public async Task GrowingLimit_ShouldWakePendingWaiters()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        await using var lease = await limiter.AcquireAsync(CancellationToken.None);

        var first = limiter.AcquireAsync(CancellationToken.None).AsTask();
        var second = limiter.AcquireAsync(CancellationToken.None).AsTask();

        limiter.UpdateLimit(3);

        var leases = await Task.WhenAll(
            first.WaitAsync(TimeSpan.FromSeconds(5)),
            second.WaitAsync(TimeSpan.FromSeconds(5)));

        foreach (var acquired in leases)
            await acquired.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_ShouldRemoveWaiterAndNotLeakCapacity()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        await using var lease = await limiter.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var pending = limiter.AcquireAsync(cts.Token).AsTask();
        cts.Cancel();

        await pending.Invoking(task => task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        limiter.PendingWaiters.Should().Be(0);

        await lease.DisposeAsync();
        var next = await limiter.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await next.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ShouldFailPendingWaiters()
    {
        var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        var lease = await limiter.AcquireAsync(CancellationToken.None);
        var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();

        await limiter.DisposeAsync();

        await pending.Invoking(task => task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<ObjectDisposedException>();

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task LeaseDisposeAsync_ShouldBeIdempotent()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        var lease = await limiter.AcquireAsync(CancellationToken.None);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        limiter.InUse.Should().Be(0);
    }

    [Fact]
    public async Task LeaseDisposeAsync_ShouldNotRunWaiterContinuationsInline()
    {
        await using var limiter = new AdaptiveInFlightLimiter(initialLimit: 1);
        var lease = await limiter.AcquireAsync(CancellationToken.None);
        var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();
        using var continuationGate = new ManualResetEventSlim(false);

        var continuation = pending.ContinueWith(
            _ => continuationGate.Wait(TimeSpan.FromSeconds(5)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var disposeTask = lease.DisposeAsync().AsTask();

        await disposeTask.WaitAsync(TimeSpan.FromMilliseconds(500));

        continuationGate.Set();
        await continuation.WaitAsync(TimeSpan.FromSeconds(5));

        var pendingLease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await pendingLease.DisposeAsync();
    }
}
