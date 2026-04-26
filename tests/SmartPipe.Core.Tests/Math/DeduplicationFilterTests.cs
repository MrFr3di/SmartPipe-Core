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
}
