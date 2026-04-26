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

    [Fact]
    public void GetFillRatio_Empty_ShouldBeZero()
    {
        var strategy = new BackpressureStrategy(100);
        strategy.GetFillRatio(0).Should().Be(0.0);
    }

    [Fact]
    public void ShouldThrottle_BelowHigh_ShouldBeFalse()
    {
        var strategy = new BackpressureStrategy(100) { HighWatermark = 0.80 };
        strategy.ShouldThrottle(60).Should().BeFalse();
    }

    [Fact]
    public void ShouldThrottle_AboveHigh_ShouldBeTrue()
    {
        var strategy = new BackpressureStrategy(100) { HighWatermark = 0.80 };
        strategy.ShouldThrottle(81).Should().BeTrue();
    }

    [Fact]
    public void IsCritical_AboveCritical_ShouldBeTrue()
    {
        var strategy = new BackpressureStrategy(100) { CriticalWatermark = 0.95 };
        strategy.IsCritical(96).Should().BeTrue();
    }

    [Fact]
    public void IsCritical_BelowCritical_ShouldBeFalse()
    {
        var strategy = new BackpressureStrategy(100) { CriticalWatermark = 0.95 };
        strategy.IsCritical(50).Should().BeFalse();
    }

    [Fact]
    public async Task ThrottleAsync_BelowHigh_ShouldNotDelay()
    {
        var strategy = new BackpressureStrategy(100) { HighWatermark = 0.80 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await strategy.ThrottleAsync(50, CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(5); // Near zero delay
    }

    [Fact]
    public async Task ThrottleAsync_AboveHigh_ShouldDelay()
    {
        var strategy = new BackpressureStrategy(100) { HighWatermark = 0.80 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await strategy.ThrottleAsync(90, CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(50); // Proportional delay
    }
}
