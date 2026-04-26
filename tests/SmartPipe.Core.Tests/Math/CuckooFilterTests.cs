using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class CuckooFilterTests
{
    [Fact]
    public void Add_ShouldSucceed()
    {
        var filter = new CuckooFilter();
        filter.Add(42UL).Should().BeTrue();
    }

    [Fact]
    public void Contains_AfterAdd_ShouldBeTrue()
    {
        var filter = new CuckooFilter();
        filter.Add(42UL);
        filter.Contains(42UL).Should().BeTrue();
    }

    [Fact]
    public void Contains_WithoutAdd_ShouldBeFalse()
    {
        var filter = new CuckooFilter();
        filter.Contains(999UL).Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldDecreaseCount()
    {
        var filter = new CuckooFilter();
        filter.Add(42UL);
        filter.Count.Should().Be(1);

        filter.Remove(42UL).Should().BeTrue();
        filter.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_NonExistent_ShouldReturnFalse()
    {
        var filter = new CuckooFilter();
        filter.Remove(999UL).Should().BeFalse();
    }

    [Fact]
    public void Contains_AfterRemove_ShouldBeFalse()
    {
        var filter = new CuckooFilter();
        filter.Add(42UL);
        filter.Remove(42UL);
        filter.Contains(42UL).Should().BeFalse();
    }
}
