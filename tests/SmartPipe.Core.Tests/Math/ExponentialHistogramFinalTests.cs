using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ExponentialHistogramFinalTests
{
    [Fact]
    public void GetPercentile_ExactBoundary_ShouldReturnCorrectBucket()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1000, bucketCount: 3);
        hist.Record(10);
        hist.Record(100);
        hist.Record(1000);
        
        hist.P50.Should().BeGreaterThan(0);
        hist.P99.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Record_ExtremeValues_ShouldNotThrow()
    {
        var hist = new ExponentialHistogram();
        hist.Record(double.MaxValue);
        hist.Record(double.MinValue / 2); // Negative
        hist.Record(0); // Zero
    }
}
