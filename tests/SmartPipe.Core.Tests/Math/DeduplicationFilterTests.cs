using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class DeduplicationFilterTests
{
    [Fact]
    public void ContainsAndAdd_FirstTime_ShouldReturnFalse()
    {
        var filter = new DeduplicationFilter();
        filter.ContainsAndAdd(42UL).Should().BeFalse();
    }

    [Fact]
    public void ContainsAndAdd_SecondTime_ShouldReturnTrue()
    {
        var filter = new DeduplicationFilter();
        filter.ContainsAndAdd(42UL);
        filter.ContainsAndAdd(42UL).Should().BeTrue();
    }

    [Fact]
    public void ItemsSeen_ShouldCountUniqueItems()
    {
        var filter = new DeduplicationFilter();
        filter.ContainsAndAdd(1UL);
        filter.ContainsAndAdd(2UL);
        filter.ContainsAndAdd(1UL); // Duplicate

        filter.ItemsSeen.Should().Be(3); // Sees all 3 calls
    }

    [Fact]
    public void DifferentIds_ShouldBeUnique()
    {
        var filter = new DeduplicationFilter(expectedItems: 10_000, falsePositiveRate: 0.001);

        int falsePositives = 0;
        for (ulong i = 0; i < 1000; i++)
        {
            if (filter.ContainsAndAdd(i))
                falsePositives++;
        }

        falsePositives.Should().Be(0); // No false positives for small set
    }

    [Fact]
    public void Constructor_WithCustomParams_ShouldWork()
    {
        var filter = new DeduplicationFilter(expectedItems: 100, falsePositiveRate: 0.01);
        filter.ContainsAndAdd(1UL).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenExpectedItemsIsInvalid(long expectedItems)
    {
        var act = () => new DeduplicationFilter(expectedItems: expectedItems);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("expectedItems");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenFalsePositiveRateIsInvalid(
        double falsePositiveRate)
    {
        var act = () => new DeduplicationFilter(falsePositiveRate: falsePositiveRate);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("falsePositiveRate");
    }

    [Fact]
    public void TtlCleanup_ShouldNotCreateFalseNegativeForNonExpiredItemSharingBits()
    {
        var ttl = TimeSpan.FromMilliseconds(120);
        var first = 1UL;
        var second = FindValueSharingSomeButNotAllBits(first, expectedItems: 10, falsePositiveRate: 0.01);
        var filter = new DeduplicationFilter(expectedItems: 10, falsePositiveRate: 0.01, ttl: ttl);

        filter.ContainsAndAdd(first).Should().BeFalse();
        Thread.Sleep(70);
        filter.ContainsAndAdd(second).Should().BeFalse();

        Thread.Sleep(70);

        filter.ContainsAndAdd(second).Should().BeTrue();
    }

    [Fact]
    public void ContainsAndAdd_WithTtl_ShouldReturnDuplicateBeforeTtlExpires()
    {
        var clock = new MutableClock(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc));
        var filter = new DeduplicationFilter(
            expectedItems: 100,
            falsePositiveRate: 0.01,
            ttl: TimeSpan.FromMinutes(1),
            clock: clock);

        filter.ContainsAndAdd(42UL).Should().BeFalse();
        clock.UtcNow = clock.UtcNow.AddSeconds(30);

        filter.ContainsAndAdd(42UL).Should().BeTrue();
    }

    [Fact]
    public void ContainsAndAdd_WithTtl_ShouldAcceptItemAfterTtlExpires()
    {
        var clock = new MutableClock(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc));
        var filter = new DeduplicationFilter(
            expectedItems: 100,
            falsePositiveRate: 0.01,
            ttl: TimeSpan.FromMinutes(1),
            clock: clock);

        filter.ContainsAndAdd(42UL).Should().BeFalse();
        clock.UtcNow = clock.UtcNow.AddMinutes(2);

        filter.ContainsAndAdd(42UL).Should().BeFalse();
    }

    [Fact]
    public void ContainsAndAdd_WithTtl_ShouldAcceptZeroTraceIdAfterTtlExpires()
    {
        var clock = new MutableClock(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc));
        var filter = new DeduplicationFilter(
            expectedItems: 100,
            falsePositiveRate: 0.01,
            ttl: TimeSpan.FromMinutes(1),
            clock: clock);

        filter.ContainsAndAdd(0UL).Should().BeFalse();
        clock.UtcNow = clock.UtcNow.AddMinutes(2);

        filter.ContainsAndAdd(0UL).Should().BeFalse();
    }

    [Fact]
    public void ContainsAndAdd_WithTtl_ShouldUseClockAdvanceWithoutSleeping()
    {
        var clock = new MutableClock(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc));
        var filter = new DeduplicationFilter(
            expectedItems: 100,
            falsePositiveRate: 0.01,
            ttl: TimeSpan.FromSeconds(10),
            clock: clock);

        filter.ContainsAndAdd(42UL).Should().BeFalse();
        clock.UtcNow = clock.UtcNow.AddSeconds(11);

        filter.ContainsAndAdd(42UL).Should().BeFalse();
    }

    private static ulong FindValueSharingSomeButNotAllBits(
        ulong first,
        long expectedItems,
        double falsePositiveRate)
    {
        var firstIndexes = GetIndexes(first, expectedItems, falsePositiveRate);

        for (ulong candidate = first + 1; candidate < 100_000; candidate++)
        {
            var candidateIndexes = GetIndexes(candidate, expectedItems, falsePositiveRate);
            if (candidateIndexes.Overlaps(firstIndexes) && !candidateIndexes.SetEquals(firstIndexes))
                return candidate;
        }

        throw new InvalidOperationException("Could not find a deterministic shared-bit candidate.");
    }

    private static HashSet<int> GetIndexes(ulong value, long expectedItems, double falsePositiveRate)
    {
        var size = (int)(-expectedItems * System.Math.Log(falsePositiveRate) / (System.Math.Log(2) * System.Math.Log(2)));
        var hashCount = System.Math.Max(1, (int)(size / (double)expectedItems * System.Math.Log(2)));
        var indexes = new HashSet<int>();
        var h1 = Hash1(value);
        var h2 = Hash2(value);

        for (int i = 0; i < hashCount; i++)
        {
            var index = (int)((h1 + (long)i * h2) % size);
            if (index < 0)
                index += size;
            indexes.Add(index);
        }

        return indexes;
    }

    private static int Hash1(ulong x)
    {
        ulong h = 14695981039346656037;
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(x >> (i * 8));
            h *= 1099511628211;
        }
        return (int)h;
    }

    private static int Hash2(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCD;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53;
        x ^= x >> 33;
        return (int)x;
    }

    private sealed class MutableClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }
}
