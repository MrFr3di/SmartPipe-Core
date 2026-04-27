using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class BackpressureStrategyTests
{
    [Fact]
    public void DefaultThresholds_ShouldBeSet()
    {
        var s = new BackpressureStrategy(100);
        s.PauseThreshold.Should().Be(0.80);
        s.ResumeThreshold.Should().Be(0.50);
        s.CriticalThreshold.Should().Be(0.95);
    }

    [Fact]
    public void ShouldPause_WhenAbovePauseThreshold()
    {
        var s = new BackpressureStrategy(100);
        s.ShouldPause(85).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotPause_WhenBelowResumeThreshold()
    {
        var s = new BackpressureStrategy(100);
        s.ShouldPause(85); // trigger pause
        s.ShouldPause(40).Should().BeFalse();
    }

    [Fact]
    public void IsCritical_WhenAboveCritical()
    {
        var s = new BackpressureStrategy(100);
        s.IsCritical(96).Should().BeTrue();
        s.IsCritical(50).Should().BeFalse();
    }

    [Fact]
    public void ShouldStayPaused_UntilResumeThreshold()
    {
        var s = new BackpressureStrategy(100);
        s.ShouldPause(85).Should().BeTrue();
        s.ShouldPause(60).Should().BeTrue(); // Still paused (above Resume=50%)
    }

    [Fact]
    public async Task ThrottleAsync_ShouldDelayWhenPaused()
    {
        var s = new BackpressureStrategy(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.ThrottleAsync(85, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(50);
    }
}
