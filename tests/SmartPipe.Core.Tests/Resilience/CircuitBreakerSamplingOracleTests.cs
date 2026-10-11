using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

[Trait("Category", "CorrectnessRegression")]
public sealed class CircuitBreakerSamplingOracleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void ExactWindow_MatchesIndependentTimestampedStream(int stepTicks)
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureRatio = 0.9,
            SamplingDuration = TimeSpan.FromTicks(50),
            MinimumThroughput = 100_000,
        }, time);
        var samples = new List<(long Tick, bool Success)>();
        long tick = 0;
        for (var index = 0; index < 300; index++)
        {
            time.Advance(TimeSpan.FromTicks(stepTicks));
            tick += stepTicks;
            var success = index % 5 != 0;
            using (var permit = breaker.AcquirePermit())
            {
                Assert.True(permit.IsAllowed);
                if (success) permit.RecordSuccess();
                else permit.RecordFailure();
            }
            samples.Add((tick, success));
            var live = samples.Where(sample => tick - sample.Tick <= 50).ToArray();
            var expected = (double)live.Count(sample => !sample.Success) / live.Length;
            Assert.Equal(expected, breaker.GetCurrentFailureRatio(), precision: 12);
            Assert.Equal(CircuitState.Closed, breaker.State);
            if (index == 149)
            {
                breaker.Reset();
                samples.Clear();
                Assert.Equal(0, breaker.GetCurrentFailureRatio());
            }
        }
    }

    [Theory]
    [InlineData(49, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 0)]
    public void ExactExpiry_IncludesEqualSamplingDuration(long elapsedTicks, double expectedRatio)
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureRatio = 0.9,
            SamplingDuration = TimeSpan.FromTicks(50),
            MinimumThroughput = 100_000,
        }, time);
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromTicks(elapsedTicks));
        Assert.Equal(expectedRatio, breaker.GetCurrentFailureRatio());
    }

    [Fact]
    public void HybridEarlyWarning_ContributesSyntheticFailureSample()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureRatio = 0.1,
            MinimumThroughput = 100_000,
        }, new FakeTimeProvider());
        breaker.RecordSuccess();
        breaker.RecordFailure();
        Assert.Equal(2d / 3, breaker.GetCurrentFailureRatio(), precision: 12);
    }
}
