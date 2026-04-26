using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ReservoirSamplerTests
{
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
    }

    [Fact]
    public void Capacity_ShouldBeConfigurable()
    {
        var sampler = new ReservoirSampler<string>(capacity: 50);
        sampler.Capacity.Should().Be(50);
    }
}
