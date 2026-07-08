using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ExponentialHistogramAdvancedTests
{
    [Fact]
    public void P99_WithManyRecords_ShouldBeGreaterThanP50()
    {
        var hist = new ExponentialHistogram();
        for (int i = 1; i <= 10000; i++)
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
        var expected = 0.1 * System.Math.Pow(1000, 9.5 / 10);

        hist.Record(1000000); // Way above max

        hist.P50.Should().BeApproximately(expected, expected * 1e-12);
    }

    [Fact]
    public void Record_ValueBelowMin_ShouldGoToFirstBucket()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1000, bucketCount: 10);
        var expected = System.Math.Pow(1000, 0.5 / 10);

        hist.Record(0.0001); // Way below min

        hist.P50.Should().BeApproximately(expected, expected * 1e-12);
    }

    [Fact]
    public void Record_ValuesAtBucketIntervalBounds_ShouldUseExpectedBucketRepresentatives()
    {
        PercentileForSingleRecord(1).Should().BeApproximately(System.Math.Sqrt(10), 1e-12);
        PercentileForSingleRecord(9.999999).Should().BeApproximately(System.Math.Sqrt(10), 1e-12);
        PercentileForSingleRecord(10).Should().BeApproximately(System.Math.Pow(10, 1.5), 1e-12);
        PercentileForSingleRecord(100).Should().BeApproximately(System.Math.Pow(10, 1.5), 1e-12);
    }

    [Fact]
    public void ThreadSafe_RecordMany()
    {
        var hist = new ExponentialHistogram();
        Parallel.For(0, 10000, i => hist.Record(i + 1));
        hist.P50.Should().BeGreaterThan(0);
        hist.P99.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPercentile_DuringConcurrentRecord_ShouldReturnObservationalSnapshot()
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 1024, bucketCount: 10);
        var observed = new double[1000];
        var minRepresentative = System.Math.Sqrt(2);
        var maxRepresentative = System.Math.Pow(2, 9.5);
        var tolerance = maxRepresentative * 1e-12;

        Parallel.For(
            0,
            observed.Length,
            i =>
            {
                hist.Record(i + 1);
                observed[i] = hist.GetPercentile(0.95);
            }
        );

        observed.Should().OnlyContain(value =>
            double.IsFinite(value)
            && value >= minRepresentative - tolerance
            && value <= maxRepresentative + tolerance
        );
        hist.P50.Should().BeGreaterThan(0);
    }

    private static double PercentileForSingleRecord(double value)
    {
        var hist = new ExponentialHistogram(minValue: 1, maxValue: 100, bucketCount: 2);
        hist.Record(value);
        return hist.GetPercentile(0.5);
    }
}
