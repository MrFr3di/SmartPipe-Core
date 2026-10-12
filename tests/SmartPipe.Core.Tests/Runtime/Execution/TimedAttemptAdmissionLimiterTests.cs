using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Runtime.Execution;

public sealed class TimedAttemptAdmissionLimiterTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RejectAtCapacity_ReleaseIsIdempotent()
    {
        var limiter = Create(TimedAttemptAdmissionMode.Reject, out _);
        var first = await limiter.AcquireAsync(null, default);
        Assert.NotNull(first);
        Assert.Null(await limiter.AcquireAsync(null, default));
        first.Dispose();
        first.Dispose();
        var second = await limiter.AcquireAsync(null, default);
        Assert.NotNull(second);
        Assert.Null(await limiter.AcquireAsync(null, default));
        second.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrExpiredWaiter_DoesNotStealLaterSlot(bool expire)
    {
        var limiter = Create(TimedAttemptAdmissionMode.Wait, out var clock);
        using var first = await limiter.AcquireAsync(null, default);
        using var cancellation = new CancellationTokenSource();
        var wait = limiter.AcquireAsync(TimeSpan.FromSeconds(1), cancellation.Token).AsTask();
        Assert.False(wait.IsCompleted);
        if (expire)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() => wait.WaitAsync(Deadline));
        }
        else
        {
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Deadline));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        first!.Dispose();
        using var next = await limiter.AcquireAsync(null, default).AsTask().WaitAsync(Deadline);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task OneRelease_AdmitsOnlyOneWaiter()
    {
        var limiter = Create(TimedAttemptAdmissionMode.Wait, out _);
        using var first = await limiter.AcquireAsync(null, default);
        var one = limiter.AcquireAsync(null, default).AsTask();
        var two = limiter.AcquireAsync(null, default).AsTask();
        Assert.False(one.IsCompleted);
        Assert.False(two.IsCompleted);
        first!.Dispose();
        using var winner = await (await Task.WhenAny(one, two)).WaitAsync(Deadline);
        var remaining = one.IsCompleted ? two : one;
        Assert.False(remaining.IsCompleted);
        winner!.Dispose();
        using var final = await remaining.WaitAsync(Deadline);
    }

    private static TimedAttemptAdmissionLimiter Create(TimedAttemptAdmissionMode mode, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider();
        return new TimedAttemptAdmissionLimiter(new TimedAttemptAdmissionOptions(1, mode), new PipelineTime(new TimeProviderPipelineClock(clock)));
    }
}
