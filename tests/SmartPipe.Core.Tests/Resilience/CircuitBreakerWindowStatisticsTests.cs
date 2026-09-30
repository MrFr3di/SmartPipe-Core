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
}
