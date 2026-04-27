using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ExponentialHistogramAdvancedTests
{
    [Fact]
    public void P99_WithManyRecords_ShouldBeGreaterThanP50()
    {
        var hist = new ExponentialHistogram();
        for (int i = 0; i < 10000; i++)
            hist.Record(i);
        hist.P99.Should().BeGreaterThan(hist.P50);
    }

    [Fact]
    public void GetPercentile_WithNoData_ShouldReturnZero()
    {
        var hist = new ExponentialHistogram();
        hist.GetPercentile(0.50).Should().Be(0);
        hist.GetPercentile(0.99).Should().Be(0);
    }

    [Fact]
    public void Record_ValueAboveMax_ShouldGoToLastBucket()
    {
        var hist = new ExponentialHistogram(minValue: 0.1, maxValue: 100, bucketCount: 10);
        hist.Record(1000000); // Way above max
        hist.P50.Should().BeGreaterThan(0); // Should not throw, goes to last bucket
    }

    [Fact]
    public void Record_ValueBelowMin_ShouldGoToFirstBucket()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1000, bucketCount: 10);
        hist.Record(0.0001); // Way below min
        hist.P50.Should().BeGreaterThan(0); // Should not throw
    }

    [Fact]
    public void ThreadSafe_RecordMany()
    {
        var hist = new ExponentialHistogram();
        Parallel.For(0, 10000, i => hist.Record(i));
        hist.P50.Should().BeGreaterThan(0);
        hist.P99.Should().BeGreaterThan(0);
    }
}
