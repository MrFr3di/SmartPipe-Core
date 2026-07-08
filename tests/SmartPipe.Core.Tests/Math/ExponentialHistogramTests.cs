using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

[Trait("Category", "CorrectnessRegression")]
public class ExponentialHistogramTests
{
    [Fact]
    public void Record_ShouldIncrementCount()
    {
        var histogram = new ExponentialHistogram();
        histogram.Record(5.0);

        histogram.P50.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Record_InvalidValue_ShouldThrow(double value)
    {
        var histogram = new ExponentialHistogram();

        var act = () => histogram.Record(value);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("value");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    public void Constructor_InvalidMinValue_ShouldThrow(double minValue)
    {
        Action act = () => _ = new ExponentialHistogram(minValue: minValue);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("minValue");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.1)]
    [InlineData(0.09)]
    public void Constructor_InvalidMaxValue_ShouldThrow(double maxValue)
    {
        Action act = () => _ = new ExponentialHistogram(minValue: 0.1, maxValue: maxValue);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxValue");
    }

    [Fact]
    public void Constructor_NumericallyIndistinguishableLogRange_ShouldThrow()
    {
        var minValue = 1e308;
        var maxValue = System.Math.BitIncrement(minValue);

        Action act = () => _ = new ExponentialHistogram(
            minValue: minValue,
            maxValue: maxValue,
            bucketCount: 4
        );

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxValue");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidBucketCount_ShouldThrow(int bucketCount)
    {
        Action act = () => _ = new ExponentialHistogram(bucketCount: bucketCount);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("bucketCount");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void GetPercentile_InvalidPercentile_ShouldThrow(double percentile)
    {
        var histogram = new ExponentialHistogram();

        Action act = () => _ = histogram.GetPercentile(percentile);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("p");
    }

    [Fact]
    public void Percentiles_ShouldBeMonotonic()
    {
        var histogram = new ExponentialHistogram();
        for (int i = 1; i <= 1000; i++)
            histogram.Record(i);

        histogram.GetPercentile(0).Should().BeLessThanOrEqualTo(histogram.P50);
        histogram.P50.Should().BeLessThanOrEqualTo(histogram.P95);
        histogram.P95.Should().BeLessThanOrEqualTo(histogram.P99);
        histogram.P99.Should().BeLessThanOrEqualTo(histogram.GetPercentile(1));
    }

    [Fact]
    public void P50_WithUniformData_ShouldBeNearCenter()
    {
        var histogram = new ExponentialHistogram(minValue: 0.1, maxValue: 1000);
        for (int i = 0; i < 1000; i++)
            histogram.Record(500);

        histogram.P50.Should().BeApproximately(500, 100);
    }

    [Fact]
    public void Record_ValueBelowMin_ShouldClampToFirstBucket()
    {
        var histogram = new ExponentialHistogram(minValue: 10, maxValue: 1000, bucketCount: 2);
        var expected = 10 * System.Math.Pow(10, 0.5);

        histogram.Record(1);

        histogram.GetPercentile(0.5).Should().BeApproximately(expected, expected * 1e-12);
    }

    [Fact]
    public void Record_ValueAboveMax_ShouldClampToLastBucket()
    {
        var histogram = new ExponentialHistogram(minValue: 10, maxValue: 1000, bucketCount: 2);
        var expected = 10 * System.Math.Pow(10, 1.5);

        histogram.Record(100_000);

        histogram.GetPercentile(0.5).Should().BeApproximately(expected, expected * 1e-12);
    }
}
