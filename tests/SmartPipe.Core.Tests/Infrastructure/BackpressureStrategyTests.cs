using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class BackpressureStrategyTests
{
    [Fact]
    public void GetFillRatio_AtCapacity_ShouldBeOne()
    {
        var s = new BackpressureStrategy(100);
        s.GetFillRatio(100).Should().Be(1.0);
    }

    [Fact]
    public void GetFillRatio_Empty_ShouldBeZero()
    {
        var s = new BackpressureStrategy(100);
        s.GetFillRatio(0).Should().Be(0.0);
    }

    [Fact]
    public async Task ThrottleAsync_WhenAboveTarget_ShouldDelay()
    {
        var s = new BackpressureStrategy(100);
        s.UpdateThroughput(500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.ThrottleAsync(90, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task ThrottleAsync_WhenAtTarget_ShouldNotDelay()
    {
        var s = new BackpressureStrategy(100);
        s.UpdateThroughput(500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.ThrottleAsync(70, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(5);
    }

    [Fact]
    public async Task ThrottleAsync_HighThroughput_LowerTarget_MoreAggressive()
    {
        var s = new BackpressureStrategy(100);
        s.UpdateThroughput(2000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.ThrottleAsync(60, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task ThrottleAsync_LowThroughput_HigherTarget_LessAggressive()
    {
        var s = new BackpressureStrategy(100);
        s.UpdateThroughput(50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.ThrottleAsync(80, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(5);
    }

    [Fact]
    public void ShouldPause_Obsolete_ReturnsFalse()
    {
        var s = new BackpressureStrategy(100);
#pragma warning disable CS0618
        s.ShouldPause(90).Should().BeFalse();
#pragma warning restore CS0618
    }

    [Fact]
    public void IsCritical_Obsolete_ReturnsTrueAbove95()
    {
        var s = new BackpressureStrategy(100);
#pragma warning disable CS0618
        s.IsCritical(96).Should().BeTrue();
        s.IsCritical(50).Should().BeFalse();
#pragma warning restore CS0618
    }
}
