using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ExponentialHistogramTests
{
    [Fact]
    public void Record_ShouldIncrementCount()
    {
        var histogram = new ExponentialHistogram();
        histogram.Record(5.0);

        histogram.P50.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Record_ZeroOrNegative_ShouldIgnore()
    {
        var histogram = new ExponentialHistogram();
        histogram.Record(0);
        histogram.Record(-1);

        histogram.P50.Should().Be(0); // No data
    }

    [Fact]
    public void Percentiles_ShouldBeMonotonic()
    {
        var histogram = new ExponentialHistogram();
        for (int i = 0; i < 1000; i++)
            histogram.Record(i);

        histogram.P50.Should().BeLessThanOrEqualTo(histogram.P95);
        histogram.P95.Should().BeLessThanOrEqualTo(histogram.P99);
    }

    [Fact]
    public void P50_WithUniformData_ShouldBeNearCenter()
    {
        var histogram = new ExponentialHistogram(minValue: 0.1, maxValue: 1000);
        for (int i = 0; i < 1000; i++)
            histogram.Record(500);

        histogram.P50.Should().BeApproximately(500, 100);
    }
}
