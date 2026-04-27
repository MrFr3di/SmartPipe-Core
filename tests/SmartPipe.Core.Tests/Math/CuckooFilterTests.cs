using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

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

    [Fact]
    public void Constructor_ZeroExpectedItems_ShouldNotThrow()
    {
        var filter = new CuckooFilter(expectedItems: 0);
        filter.Add(42UL).Should().BeTrue();
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
}
