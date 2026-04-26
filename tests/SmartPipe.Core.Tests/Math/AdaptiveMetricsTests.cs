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
        metrics.Update(101.0); // α=0.2: 0.2*101 + 0.8*100 = 100.2

        metrics.SmoothLatencyMs.Should().BeApproximately(100.2, 0.01);
    }

    [Fact]
    public async Task Update_ShouldTrackThroughput()
    {
        var metrics = new AdaptiveMetrics();
        metrics.Update(10.0);
        await Task.Delay(100); // Ensure significant elapsed time for TickCount64
        metrics.Update(12.0);

        // Throughput should be computed after at least two updates with time gap
        metrics.SmoothThroughputPerSec.Should().BeGreaterThan(0);
    }
}
