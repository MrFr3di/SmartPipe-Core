using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class AdaptiveParallelismTests
{
    [Fact]
    public void Initial_ShouldBeProcessorCount()
    {
        var ap = new AdaptiveParallelism();
        ap.Current.Should().Be(Environment.ProcessorCount);
    }

    [Fact]
    public void HighLatency_ShouldIncreaseParallelism()
    {
        var ap = new AdaptiveParallelism(min: 2, max: 16);
        int initial = ap.Current;

        // Push latency up significantly
        for (int i = 0; i < 50; i++)
            ap.Update(100, 20);

        ap.Current.Should().BeGreaterThanOrEqualTo(initial);
    }

    [Fact]
    public void LowLatency_ShouldDecreaseParallelism()
    {
        var ap = new AdaptiveParallelism(min: 2, max: 16);
        int initial = ap.Current;

        // Push latency down
        for (int i = 0; i < 50; i++)
            ap.Update(1, 0);

        ap.Current.Should().BeLessThanOrEqualTo(initial);
    }

    [Fact]
    public void ShouldRespectMinMax()
    {
        var ap = new AdaptiveParallelism(min: 4, max: 8);

        for (int i = 0; i < 100; i++) ap.Update(0.1, 0);
        ap.Current.Should().BeGreaterThanOrEqualTo(4);

        for (int i = 0; i < 100; i++) ap.Update(1000, 100);
        ap.Current.Should().BeLessThanOrEqualTo(8);
    }

    [Fact]
    public void DeadZone_ShouldNotChangeOnSmallErrors()
    {
        var ap = new AdaptiveParallelism(min: 2, max: 16);
        int initial = ap.Current;

        // Small error (< 5ms) should not change parallelism
        for (int i = 0; i < 10; i++)
            ap.Update(10.5, 0); // ~10ms vs target 10ms

        ap.Current.Should().Be(initial);
    }
}
