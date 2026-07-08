using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class JumpHashTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Hash_ShouldThrowArgumentOutOfRangeException_WhenNumBucketsIsInvalid(int numBuckets)
    {
        var act = () => JumpHash.Hash(42UL, numBuckets);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("numBuckets");
    }

    [Fact]
    public void Hash_ShouldReturnValidBucket()
    {
        int bucket = JumpHash.Hash(42UL, 10);
        bucket.Should().BeInRange(0, 9);
    }

    [Fact]
    public void Hash_SameKey_ShouldReturnSameBucket()
    {
        int b1 = JumpHash.Hash(123UL, 10);
        int b2 = JumpHash.Hash(123UL, 10);

        b1.Should().Be(b2);
    }

    [Fact]
    public void Hash_DifferentKeys_MayBeDifferent()
    {
        var buckets = new HashSet<int>();
        for (ulong i = 0; i < 100; i++)
            buckets.Add(JumpHash.Hash(i, 10));

        buckets.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Hash_ShouldBeDeterministic()
    {
        var results = new List<int>();
        for (int i = 0; i < 100; i++)
            results.Add(JumpHash.Hash(42UL, 100));

        results.Should().AllBeEquivalentTo(results[0]);
    }

    [Theory]
    [InlineData(0UL, 1, 0)]
    [InlineData(0UL, 2, 0)]
    [InlineData(0UL, 10, 0)]
    [InlineData(1UL, 1, 0)]
    [InlineData(1UL, 2, 0)]
    [InlineData(1UL, 10, 6)]
    [InlineData(42UL, 10, 2)]
    [InlineData(123UL, 10, 0)]
    [InlineData(123456789UL, 1000, 294)]
    [InlineData(ulong.MaxValue, 1000, 313)]
    public void Hash_ShouldMatchLampingVeachReferenceImplementation(
        ulong key,
        int numBuckets,
        int expectedBucket)
    {
        JumpHash.Hash(key, numBuckets).Should().Be(expectedBucket);
    }
}
