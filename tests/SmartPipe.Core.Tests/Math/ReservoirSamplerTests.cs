using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ReservoirSamplerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenCapacityIsInvalid(int capacity)
    {
        var act = () => new ReservoirSampler<int>(capacity);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("capacity");
    }

    [Fact]
    public void Add_LessThanCapacity_ShouldStoreAll()
    {
        var sampler = new ReservoirSampler<int>(10);
        for (int i = 0; i < 5; i++)
            sampler.Add(i);

        sampler.Count.Should().Be(5);
        sampler.Sample.Take(5).Should().BeEquivalentTo([0, 1, 2, 3, 4]);
    }

    [Fact]
    public void Sample_ShouldReturnSnapshotWithOnlyPopulatedItems()
    {
        var sampler = new ReservoirSampler<int>(10);
        sampler.Add(1);
        sampler.Add(2);
        sampler.Add(3);

        sampler.Sample.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public void GetSampleSnapshot_ShouldReturnCopy()
    {
        var sampler = new ReservoirSampler<int>(10);
        sampler.Add(1);
        sampler.Add(2);

        var snapshot = sampler.GetSampleSnapshot();
        snapshot[0] = 42;

        sampler.GetSampleSnapshot().Should().Equal([1, 2]);
    }

    [Fact]
    public void Add_MoreThanCapacity_ShouldMaintainSize()
    {
        var sampler = new ReservoirSampler<int>(10);
        for (int i = 0; i < 1000; i++)
            sampler.Add(i);

        sampler.Count.Should().Be(1000);
        sampler.Sample.Should().OnlyContain(x => x >= 0 && x < 1000);
    }

    [Fact]
    public void Reset_ShouldClearAll()
    {
        var sampler = new ReservoirSampler<int>(10);
        for (int i = 0; i < 100; i++)
            sampler.Add(i);

        sampler.Reset();
        sampler.Count.Should().Be(0);
        sampler.GetSampleSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void Capacity_ShouldBeConfigurable()
    {
        var sampler = new ReservoirSampler<string>(capacity: 50);
        sampler.Capacity.Should().Be(50);
    }
}
