#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveConcurrencyLimiterTests
{
    [Fact]
    public void Constructor_RejectsInitialLimitLessThanOne()
    {
        var act = () => new AdaptiveConcurrencyLimiter(initialLimit: 0, maxLimit: 1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("initialLimit");
    }

    [Fact]
    public void Constructor_RejectsMaxLimitLessThanInitial()
    {
        var act = () => new AdaptiveConcurrencyLimiter(initialLimit: 2, maxLimit: 1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("maxLimit");
    }

    [Fact]
    public async Task AcquireAsync_InitialLimitAllowsOnlyInitialConcurrency()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 2, maxLimit: 4);

        var first = await limiter.AcquireAsync();
        var second = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync();

        limiter.CurrentLimit.Should().Be(2);
        limiter.InFlight.Should().Be(2);
        queued.IsCompleted.Should().BeFalse();

        first.Dispose();
        var third = await queued;

        limiter.InFlight.Should().Be(2);

        second.Dispose();
        third.Dispose();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenLimitReached_QueuesWaiter()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 2);

        var lease = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync();

        queued.IsCompleted.Should().BeFalse();
        limiter.InFlight.Should().Be(1);

        lease.Dispose();
        var released = await queued;

        limiter.InFlight.Should().Be(1);
        released.Dispose();
    }

    [Fact]
    public async Task UpdateLimit_Grow_ReleasesQueuedWaiter()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 3);

        var first = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync();

        queued.IsCompleted.Should().BeFalse();

        limiter.UpdateLimit(2);
        var second = await queued;

        limiter.CurrentLimit.Should().Be(2);
        limiter.InFlight.Should().Be(2);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task UpdateLimit_Shrink_KeepsInFlightAndBlocksNewAcquires()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 3, maxLimit: 3);

        var first = await limiter.AcquireAsync();
        var second = await limiter.AcquireAsync();
        var third = await limiter.AcquireAsync();

        limiter.UpdateLimit(1);
        var queued = limiter.AcquireAsync();

        limiter.CurrentLimit.Should().Be(1);
        limiter.InFlight.Should().Be(3);
        queued.IsCompleted.Should().BeFalse();

        first.Dispose();
        queued.IsCompleted.Should().BeFalse();
        limiter.InFlight.Should().Be(2);

        second.Dispose();
        queued.IsCompleted.Should().BeFalse();
        limiter.InFlight.Should().Be(1);

        third.Dispose();
        var replacement = await queued;

        limiter.InFlight.Should().Be(1);
        replacement.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_CancelledWaiter_DoesNotLeakPermit()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        using var cts = new CancellationTokenSource();

        var lease = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync(cts.Token).AsTask();

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => queued)
            .Should().ThrowAsync<OperationCanceledException>();
        limiter.InFlight.Should().Be(1);

        lease.Dispose();
        var replacement = await limiter.AcquireAsync();

        limiter.InFlight.Should().Be(1);
        replacement.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_PreCancelledToken_ReturnsCanceledTask()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = limiter.AcquireAsync(cts.Token).AsTask();

        task.IsCanceled.Should().BeTrue();
        await FluentActions.Awaiting(() => task)
            .Should().ThrowAsync<OperationCanceledException>();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task ManyCancelledWaiters_AreSkippedOnNextRelease_AndDoNotLeakPermit()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        using var cts = new CancellationTokenSource();
        var active = await limiter.AcquireAsync();
        var cancelledWaiters = Enumerable.Range(0, 32)
            .Select(_ => limiter.AcquireAsync(cts.Token).AsTask())
            .ToArray();
        var liveWaiter = limiter.AcquireAsync().AsTask();

        await cts.CancelAsync();
        await Task.WhenAll(cancelledWaiters.Select(async waiter =>
        {
            await FluentActions.Awaiting(() => waiter)
                .Should().ThrowAsync<OperationCanceledException>();
        }));

        limiter.InFlight.Should().Be(1);
        active.Dispose();

        var liveLease = await liveWaiter;
        limiter.InFlight.Should().Be(1);

        liveLease.Dispose();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_UnblocksQueuedWaiters()
    {
        var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        var lease = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync().AsTask();

        limiter.Dispose();

        await FluentActions.Awaiting(() => queued)
            .Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(async () => await limiter.AcquireAsync())
            .Should().ThrowAsync<ObjectDisposedException>();

        lease.Dispose();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);

        limiter.Dispose();
        var act = () => limiter.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);

        await limiter.DisposeAsync();
        var act = async () => await limiter.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);

        limiter.Complete();
        var act = () => limiter.Complete();

        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateLimit_RejectsLimitLessThanOne()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 2);

        var act = () => limiter.UpdateLimit(0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("newLimit");
    }

    [Fact]
    public void UpdateLimit_RejectsLimitGreaterThanMax()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 2);

        var act = () => limiter.UpdateLimit(3);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("newLimit");
    }

    [Fact]
    public void UpdateLimit_AfterDispose_ThrowsObjectDisposedException()
    {
        var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 2);
        limiter.Dispose();

        var act = () => limiter.UpdateLimit(1);

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(AdaptiveConcurrencyLimiter));
    }

    [Fact]
    public async Task ConcurrentAcquireRelease_NeverExceedsLimit()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 4, maxLimit: 4);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxObserved = 0;

        var workers = Enumerable.Range(0, 128)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                var lease = await limiter.AcquireAsync();
                var current = Interlocked.Increment(ref active);
                TrackMax(ref maxObserved, current);

                try
                {
                    await Task.Yield();
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    lease.Dispose();
                }
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(workers);

        maxObserved.Should().BeLessThanOrEqualTo(4);
        active.Should().Be(0);
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Lease_DisposeTwice_IsNoOpAfterFirstRelease()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        var lease = await limiter.AcquireAsync();

        lease.Dispose();
        var act = () => lease.Dispose();

        act.Should().NotThrow();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public void Lease_DefaultDispose_IsNoOp()
    {
        var lease = default(AdaptiveConcurrencyLimiter.Lease);

        var act = () => lease.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Lease_DisposeAsyncTwice_IsNoOpAfterFirstRelease()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        var lease = await limiter.AcquireAsync();

        await lease.DisposeAsync();
        var act = async () => await lease.DisposeAsync();

        await act.Should().NotThrowAsync();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Release_QueuedWaiterContinuation_DoesNotRunInline()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(initialLimit: 1, maxLimit: 1);
        var lease = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync().AsTask();
        var gate = new object();
        var ranInline = false;

        lock (gate)
        {
            _ = queued.ContinueWith(
                _ =>
                {
                    lock (gate)
                    {
                        ranInline = true;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            lease.Dispose();

            ranInline.Should().BeFalse();
        }

        var released = await queued;
        released.Dispose();
    }

    private static void TrackMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current)
                return;

            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
