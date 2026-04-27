using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class HyperLogLogEstimatorTests
{
    [Fact]
    public void Constructor_ShouldCreate() => new HyperLogLogEstimator().Should().NotBeNull();

    [Fact]
    public void InvalidPrecision_ShouldThrow()
    {
        Action act = () => new HyperLogLogEstimator(3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Estimate_10000Unique_ShouldBeRoughlyCorrect()
    {
        var hll = new HyperLogLogEstimator(12);
        for (ulong i = 0; i < 10000; i++) hll.Add(i);
        var est = hll.Estimate();
        // HLL with precision 12 has ~3% error
        est.Should().BeGreaterThan(3000);
        est.Should().BeLessThan(30000);
    }

    [Fact]
    public void Merge_ShouldCombine()
    {
        var a = new HyperLogLogEstimator(12);
        var b = new HyperLogLogEstimator(12);
        for (ulong i = 0; i < 5000; i++) a.Add(i);
        for (ulong i = 3000; i < 8000; i++) b.Add(i);
        HyperLogLogEstimator.Merge(a, b).Estimate().Should().BeGreaterThan(5000);
    }
}
