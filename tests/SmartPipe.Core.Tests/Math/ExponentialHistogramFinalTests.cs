using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ExponentialHistogramFinalTests
{
    [Fact]
    public void GetPercentile_ExactBoundary_ShouldReturnCorrectBucket()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1000, bucketCount: 3);
        var expectedMedian = System.Math.Pow(10, 1.5);
        var expectedTail = System.Math.Pow(10, 2.5);

        hist.Record(1);
        hist.Record(10);
        hist.Record(100);

        hist.P50.Should().BeApproximately(expectedMedian, expectedMedian * 1e-12);
        hist.P99.Should().BeApproximately(expectedTail, expectedTail * 1e-12);
    }

    [Fact]
    public void Record_FinitePositiveExtremeValue_ShouldClampToLastBucket()
    {
        var hist = new ExponentialHistogram();

        hist.Record(double.MaxValue);

        hist.P50.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Record_FiniteAboveRangeValueWithNarrowRange_ShouldClampToLastBucket()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1.000000000001, bucketCount: 4);

        hist.Record(double.MaxValue);

        GetBuckets(hist).Should().Equal(0, 0, 0, 1);
    }

    [Fact]
    public void GetPercentile_WithWideFiniteRange_ShouldReturnFiniteRepresentative()
    {
        var hist = new ExponentialHistogram(
            minValue: double.Epsilon,
            maxValue: double.MaxValue,
            bucketCount: 1
        );

        hist.Record(double.MaxValue);

        double.IsFinite(hist.P50).Should().BeTrue();
    }

    [Fact]
    public void GetPercentile_ZeroAndOne_ShouldReturnFirstAndLastObservedBuckets()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1000, bucketCount: 3);
        var expectedFirst = System.Math.Pow(10, 0.5);
        var expectedLast = System.Math.Pow(10, 2.5);

        hist.Record(1);
        hist.Record(1000);

        hist.GetPercentile(0).Should().BeApproximately(expectedFirst, expectedFirst * 1e-12);
        hist.GetPercentile(1).Should().BeApproximately(expectedLast, expectedLast * 1e-12);
    }

    private static long[] GetBuckets(ExponentialHistogram histogram)
    {
        var field = typeof(ExponentialHistogram).GetField(
            "_buckets",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );

        field.Should().NotBeNull();
        return ((long[]?)field!.GetValue(histogram))!;
    }
}
