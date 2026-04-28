using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class AdaptiveMetricsTests
{
    [Fact]
    public void Initial_ShouldBeZero()
    {
        var metrics = new AdaptiveMetrics();
        metrics.SmoothLatencyMs.Should().Be(0);
        metrics.SmoothThroughputPerSec.Should().Be(0);
        metrics.LatencyVelocity.Should().Be(0);
    }

    [Fact]
    public void Update_ShouldSetInitialValue()
    {
        var metrics = new AdaptiveMetrics();
        metrics.Update(50.0);
        metrics.SmoothLatencyMs.Should().Be(50.0);
    }

    [Fact]
    public void Update_ShouldApplyEMA()
    {
        var metrics = new AdaptiveMetrics();
        metrics.Update(100.0);
        metrics.Update(101.0);
        metrics.SmoothLatencyMs.Should().BeApproximately(100.2, 0.01);
    }

    [Fact]
    public async Task Update_ShouldTrackThroughput()
    {
        var metrics = new AdaptiveMetrics();
        metrics.Update(10.0);
        await Task.Delay(50);
        metrics.Update(12.0);
        metrics.SmoothThroughputPerSec.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PredictNextLatency_ShouldBeNonNegative()
    {
        var metrics = new AdaptiveMetrics();
        metrics.Update(10.0);
        metrics.Update(15.0);
        metrics.PredictNextLatency().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void LatencyVelocity_ShouldTrackTrend()
    {
        var metrics = new AdaptiveMetrics();
        metrics.Update(10.0);
        metrics.Update(15.0);
        metrics.LatencyVelocity.Should().BeGreaterThan(0); // Rising
    }
}
