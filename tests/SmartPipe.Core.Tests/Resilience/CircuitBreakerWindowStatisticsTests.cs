using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

[Trait("Category", "CorrectnessRegression")]
public sealed class CircuitBreakerWindowStatisticsTests
{
    [Fact]
    public void FailureRatio_TracksExpiryAndResetIncrementally()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                FailureRatio = 0.9,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 100,
            },
            time);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.GetCurrentFailureRatio().Should().BeApproximately(2d / 3d, 1e-9);

        time.Advance(TimeSpan.FromSeconds(11));
        breaker.RecordSuccess();
        breaker.GetCurrentFailureRatio().Should().Be(0);

        breaker.RecordFailure();
        breaker.GetCurrentFailureRatio().Should().Be(0.5);

        breaker.Reset();
        breaker.GetCurrentFailureRatio().Should().Be(0);
        breaker.RecordFailure();
        breaker.GetCurrentFailureRatio().Should().Be(1);
    }

    [Fact]
    public void SlidingWindow_OpensOnlyWhenRatioWithinWindowReachesThreshold()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 6,
            },
            time);

        breaker.RecordFailure();
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));
        for (var index = 0; index < 4; index++)
            breaker.RecordSuccess();

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Closed, "expired failures must no longer count");

        breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public async Task ConcurrentRecords_KeepWindowInTimestampOrder()
    {
        var time = new GatedFirstReadTimeSource(firstTimestamp: 100, laterTimestamp: 200);
        var breaker = new CircuitBreaker(
            failureRatio: 0.9,
            samplingDuration: TimeSpan.FromTicks(1_000),
            minimumThroughput: 100,
            breakDuration: TimeSpan.FromSeconds(30),
            maxHalfOpenRequests: 1,
            time);

        var first = Task.Run(breaker.RecordFailure);
        await time.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(breaker.RecordSuccess);

        // The later sample cannot be stamped and enqueued ahead of the earlier, still-pending one.
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();

        time.ReleaseFirstRead();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        var window = (Queue<(long Timestamp, bool IsSuccess)>)typeof(CircuitBreaker)
            .GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(breaker)!;
        window.Select(sample => sample.Timestamp).Should().BeInAscendingOrder();
        breaker.GetCurrentFailureRatio().Should().Be(0.5);
    }

    private sealed class GatedFirstReadTimeSource(long firstTimestamp, long laterTimestamp) : ICircuitBreakerTimeSource
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _reads;

        public TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTime UtcNow => new(laterTimestamp, DateTimeKind.Utc);

        public long GetTimestamp()
        {
            if (Interlocked.Increment(ref _reads) != 1)
                return laterTimestamp;

            FirstReadStarted.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(10));
            return firstTimestamp;
        }

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void ReleaseFirstRead() => _release.Set();
    }
}
