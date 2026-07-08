using FluentAssertions;
using SmartPipe.Core;
using System.Reflection;

namespace SmartPipe.Core.Tests.Math;

[Trait("Category", "CorrectnessRegression")]
public class CuckooFilterTests
{
    [Fact]
    public void Add_ShouldSucceed()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Add(42UL).Should().BeTrue();
    }

    [Fact]
    public void Contains_AfterAdd_ShouldBeTrue()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Add(42UL);
        filter.Contains(42UL).Should().BeTrue();
    }

    [Fact]
    public void Contains_WithoutAdd_ShouldBeFalse()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Contains(999UL).Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldDecreaseCount()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Add(42UL);
        filter.Count.Should().Be(1);
        filter.Remove(42UL).Should().BeTrue();
        filter.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_NonExistent_ShouldReturnFalse()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Remove(999UL).Should().BeFalse();
    }

    [Fact]
    public void Contains_AfterRemove_ShouldBeFalse()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Add(42UL);
        filter.Remove(42UL);
        filter.Contains(42UL).Should().BeFalse();
    }

    [Fact]
    public void Add_ManyItems_ShouldNotLoseCount()
    {
        var filter = new CuckooFilter(expectedItems: 1000);
        int added = 0;
        for (ulong i = 0; i < 100; i++)
            if (filter.Add(i)) added++;
        filter.Count.Should().Be(added);
    }

    [Fact]
    public void Contains_Zero_ShouldNotThrow()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Invoking(f => f.Contains(0UL)).Should().NotThrow();
    }

    [Fact]
    public void Remove_Zero_ShouldNotThrow()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Invoking(f => f.Remove(0UL)).Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidExpectedItems_ShouldThrow(long expectedItems)
    {
        var act = () => new CuckooFilter(expectedItems: expectedItems);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("expectedItems");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_InvalidFalsePositiveRate_ShouldThrow(double falsePositiveRate)
    {
        var act = () => new CuckooFilter(expectedItems: 100, falsePositiveRate: falsePositiveRate);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("falsePositiveRate");
    }

    [Fact]
    public void Constructor_FalsePositiveRateRequiringMoreThan32Bits_ShouldThrow()
    {
        var act = () => new CuckooFilter(expectedItems: 100, falsePositiveRate: 1e-12);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("falsePositiveRate");
    }

    [Fact]
    public void FailedAdd_MustRestoreExactBucketState()
    {
        var filter = new CuckooFilter(expectedItems: 1);

        for (ulong value = 1; value < 10_000; value++)
        {
            var before = CaptureBuckets(filter);

            if (filter.Add(value))
                continue;

            var after = CaptureBuckets(filter);
            BucketsShouldEqual(after, before, $"failed Add({value}) must restore the exact bucket matrix");
            return;
        }

        throw new InvalidOperationException("Test did not saturate the filter.");
    }

    [Fact]
    public void FailedAdd_SingleBucket_ShouldReturnWithoutKickLoopAndPreserveState()
    {
        var filter = new CuckooFilter(expectedItems: 1);
        GetBucketCount(filter).Should().Be(1);
        var inserted = new List<ulong>();

        for (ulong value = 1; value < 10_000 && filter.Count < 4; value++)
        {
            if (filter.Add(value))
                inserted.Add(value);
        }

        inserted.Should().HaveCount(4);
        var beforeBuckets = CaptureBuckets(filter);
        var beforeCount = filter.Count;

        filter.Add(10_001UL).Should().BeFalse();

        BucketsShouldEqual(CaptureBuckets(filter), beforeBuckets);
        filter.Count.Should().Be(beforeCount);
        foreach (var value in inserted)
            filter.Contains(value).Should().BeTrue();
    }

    [Fact]
    public void AlternateBucket_MustBeInvolution()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        var bucketCount = GetBucketCount(filter);
        const uint fingerprint = 42;

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var alternate = InvokeAlternateBucket(filter, bucket, fingerprint);
            InvokeAlternateBucket(filter, alternate, fingerprint).Should().Be(bucket);
        }
    }

    [Fact]
    public void AlternateBucket_ShouldDifferFromSourceBucket_WhenMoreThanOneBucketExists()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        var bucketCount = GetBucketCount(filter);
        bucketCount.Should().BeGreaterThan(1);
        const uint fingerprint = 42;

        for (var bucket = 0; bucket < bucketCount; bucket++)
            InvokeAlternateBucket(filter, bucket, fingerprint).Should().NotBe(bucket);
    }

    [Fact]
    public void DifferentFalsePositiveRates_MustProduceDifferentFingerprintWidths()
    {
        var loose = new CuckooFilter(expectedItems: 100, falsePositiveRate: 0.1);
        var strict = new CuckooFilter(expectedItems: 100, falsePositiveRate: 0.001);

        GetFingerprintBits(strict).Should().BeGreaterThan(GetFingerprintBits(loose));
    }

    [Fact]
    public void Merge_WhenDestinationCannotFitAllEntries_ShouldRestoreDestinationExactly()
    {
        var destination = new CuckooFilter(expectedItems: 1);
        var source = new CuckooFilter(expectedItems: 1);
        FillUntilFull(destination);
        source.Add(FindValueNotRepresented(destination)).Should().BeTrue();
        var beforeBuckets = CaptureBuckets(destination);
        var beforeCount = destination.Count;

        var act = () => destination.Merge(source);

        act.Should().Throw<InvalidOperationException>();
        BucketsShouldEqual(CaptureBuckets(destination), beforeBuckets);
        destination.Count.Should().Be(beforeCount);
    }

    [Fact]
    public void Merge_WithIncompatibleLayout_ShouldThrow()
    {
        var destination = new CuckooFilter(expectedItems: 10, falsePositiveRate: 0.001);
        var source = new CuckooFilter(expectedItems: 10, falsePositiveRate: 0.1);

        var act = () => destination.Merge(source);

        act.Should().Throw<ArgumentException>().WithParameterName("other");
    }

    [Fact]
    public void Merge_WithRepresentedFingerprintFromDistinctSourceEntry_ShouldIncreaseOccupancy()
    {
        var destination = new CuckooFilter(expectedItems: 100);
        var source = new CuckooFilter(expectedItems: 100);
        destination.Add(42).Should().BeTrue();
        source.Add(42).Should().BeTrue();
        destination.Contains(42).Should().BeTrue();
        var beforeCount = destination.Count;
        var beforeOccupancy = CountOccupiedSlots(destination);

        destination.Merge(source);

        destination.Count.Should().Be(beforeCount + 1);
        CountOccupiedSlots(destination).Should().Be(beforeOccupancy + 1);
    }

    [Fact]
    public void Merge_Self_ShouldBeNoOp()
    {
        var filter = new CuckooFilter(expectedItems: 100);
        filter.Add(42);
        filter.Add(43);
        var beforeBuckets = CaptureBuckets(filter);
        var beforeCount = filter.Count;

        filter.Merge(filter);

        BucketsShouldEqual(CaptureBuckets(filter), beforeBuckets);
        filter.Count.Should().Be(beforeCount);
    }

    [Fact]
    public void Add_UntilFull_ShouldHandleKicks()
    {
        var filter = new CuckooFilter(expectedItems: 10);
        int added = 0;
        for (ulong i = 0; i < 50; i++)
            if (filter.Add(i)) added++;
        added.Should().BeLessThanOrEqualTo(50);
        filter.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "ConcurrencyRegression")]
    public void ThreadSafe_AddRemove()
    {
        var filter = new CuckooFilter(expectedItems: 1000);
        var errors = 0;
        Parallel.For(0, 100, i =>
        {
            try
            {
                filter.Add((ulong)i);
                if (i % 3 == 0) filter.Remove((ulong)i);
            }
            catch { Interlocked.Increment(ref errors); }
        });
        errors.Should().Be(0);
    }

    private static uint[,] CaptureBuckets(CuckooFilter filter)
    {
        var buckets = (uint[,])GetField("_buckets").GetValue(filter)!;
        return (uint[,])buckets.Clone();
    }

    private static void BucketsShouldEqual(uint[,] actual, uint[,] expected, string because = "")
    {
        actual.GetLength(0).Should().Be(expected.GetLength(0), because);
        actual.GetLength(1).Should().Be(expected.GetLength(1), because);

        for (var bucket = 0; bucket < actual.GetLength(0); bucket++)
            for (var slot = 0; slot < actual.GetLength(1); slot++)
                actual[bucket, slot].Should().Be(expected[bucket, slot], because);
    }

    private static int CountOccupiedSlots(CuckooFilter filter)
    {
        var buckets = CaptureBuckets(filter);
        var occupied = 0;

        for (var bucket = 0; bucket < buckets.GetLength(0); bucket++)
            for (var slot = 0; slot < buckets.GetLength(1); slot++)
                if (buckets[bucket, slot] != 0)
                    occupied++;

        return occupied;
    }

    private static int GetBucketCount(CuckooFilter filter) =>
        (int)GetField("_numBuckets").GetValue(filter)!;

    private static int GetFingerprintBits(CuckooFilter filter) =>
        (int)GetField("_fingerprintBits").GetValue(filter)!;

    private static int InvokeAlternateBucket(CuckooFilter filter, int bucket, uint fingerprint)
    {
        var method = typeof(CuckooFilter).GetMethod(
            "AlternateBucket",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (int)method!.Invoke(filter, [bucket, fingerprint])!;
    }

    private static FieldInfo GetField(string name)
    {
        var field = typeof(CuckooFilter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return field!;
    }

    private static void FillUntilFull(CuckooFilter filter)
    {
        for (ulong value = 1; value < 10_000; value++)
        {
            if (!filter.Add(value))
                return;
        }

        throw new InvalidOperationException("Test did not saturate the filter.");
    }

    private static ulong FindValueNotRepresented(CuckooFilter filter)
    {
        for (ulong value = 10_001; value < 100_000; value++)
        {
            if (!filter.Contains(value))
                return value;
        }

        throw new InvalidOperationException("Test could not find a non-represented value.");
    }
}
