using System.Reflection;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

[Trait("Category", "CorrectnessRegression")]
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
    public void Estimate_WithHighRankRegisters_ShouldUseFloatingPointPowerOfTwo()
    {
        const int precision = 12;
        var estimator = new HyperLogLogEstimator(precision);
        var registers = GetRegisters(estimator);

        Array.Fill(registers, (byte)40);
        var actual = estimator.Estimate();
        var m = registers.Length;
        var alpha = 0.7213 / (1 + 1.079 / m);
        var expected = alpha * m * m / (m * global::System.Math.ScaleB(1.0, -40));

        actual.Should().BeApproximately(expected, expected * 1e-12);
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

    [Fact]
    public void Merge_WithNullArray_ShouldThrow()
    {
        HyperLogLogEstimator[] estimators = null!;

        Action act = () => HyperLogLogEstimator.Merge(estimators);

        act.Should().Throw<ArgumentNullException>().WithParameterName("es");
    }

    [Fact]
    public void Merge_WithEmptyArray_ShouldThrow()
    {
        Action act = () => HyperLogLogEstimator.Merge(Array.Empty<HyperLogLogEstimator>());

        act.Should().Throw<ArgumentException>().WithParameterName("es");
    }

    [Fact]
    public void Merge_WithNullElement_ShouldThrow()
    {
        var estimators = new HyperLogLogEstimator[] { new(12), null! };

        Action act = () => HyperLogLogEstimator.Merge(estimators);

        act.Should().Throw<ArgumentNullException>().WithParameterName("es");
    }

    [Fact]
    public void Merge_WithPrecisionMismatch_ShouldThrow()
    {
        Action act = () => HyperLogLogEstimator.Merge(new HyperLogLogEstimator(12), new HyperLogLogEstimator(10));

        act.Should().Throw<ArgumentException>().WithParameterName("es");
    }

    private static byte[] GetRegisters(HyperLogLogEstimator estimator)
    {
        var field = typeof(HyperLogLogEstimator).GetField(
            "_regs",
            BindingFlags.Instance | BindingFlags.NonPublic);

        return (byte[])field!.GetValue(estimator)!;
    }
}
