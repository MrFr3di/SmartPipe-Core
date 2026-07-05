using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class BackpressureStrategyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenCapacityIsNotPositive(
        int capacity)
    {
        var act = () => new BackpressureStrategy(capacity);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("capacity");
    }

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
    public void GetFillRatio_WhenCurrentSizeExceedsCapacity_ShouldClampToOne()
    {
        var s = new BackpressureStrategy(100);

        s.GetFillRatio(150).Should().Be(1.0);
    }

    [Fact]
    public void GetFillRatio_ShouldThrowArgumentOutOfRangeException_WhenCurrentSizeIsNegative()
    {
        var s = new BackpressureStrategy(100);
        var act = () => s.GetFillRatio(-1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("currentSize");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    public void UpdateThroughput_ShouldThrowArgumentOutOfRangeException_WhenThroughputIsInvalid(
        double throughputPerSec)
    {
        var s = new BackpressureStrategy(100);
        var act = () => s.UpdateThroughput(throughputPerSec);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("throughputPerSec");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    public void UpdateThroughput_ShouldThrowArgumentOutOfRangeException_WhenPredictedLatencyIsInvalid(
        double predictedLatencyMs)
    {
        var s = new BackpressureStrategy(100);
        var act = () => s.UpdateThroughput(500, predictedLatencyMs);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("predictedLatencyMs");
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
}
