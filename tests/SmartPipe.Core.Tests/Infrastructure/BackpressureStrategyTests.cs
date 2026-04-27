using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class BackpressureStrategyTests
{
    [Fact]
    public void GetFillRatio_AtCapacity_ShouldBeOne()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.GetFillRatio(100).Should().Be(1.0);
    }

    [Fact]    public void GetFillRatio_Empty_ShouldBeZero()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.GetFillRatio(0).Should().Be(0.0);
    }

    [Fact]
    public void ShouldThrottle_HighThroughput_ShouldUseHighWatermark()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.UpdateThroughput(2000); // High throughput > 1000
        strategy.ShouldThrottle(92).Should().BeTrue(); // > 0.90
        strategy.ShouldThrottle(85).Should().BeFalse(); // < 0.90
    }

    [Fact]
    public void ShouldThrottle_LowThroughput_ShouldUseLowWatermark()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.UpdateThroughput(50); // Low throughput < 100
        strategy.ShouldThrottle(60).Should().BeTrue(); // > 0.50
    }

    [Fact]
    public void IsCritical_HighThroughput_ShouldUseHighCritical()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.UpdateThroughput(2000);
        strategy.IsCritical(99).Should().BeTrue(); // > 0.98
        strategy.IsCritical(95).Should().BeFalse(); // < 0.98
    }

    [Fact]
    public void IsCritical_LowThroughput_ShouldUseLowCritical()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.UpdateThroughput(50);
        strategy.IsCritical(85).Should().BeTrue(); // > 0.80
    }

    [Fact]
    public async Task ThrottleAsync_BelowHigh_ShouldNotDelay()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.UpdateThroughput(500); // Medium
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await strategy.ThrottleAsync(50, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(10);
    }

    [Fact]
    public async Task ThrottleAsync_AboveHigh_ShouldDelay()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.UpdateThroughput(500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await strategy.ThrottleAsync(85, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(50);
    }
}
