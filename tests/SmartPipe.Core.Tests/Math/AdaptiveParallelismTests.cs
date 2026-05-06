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
    public void HighLatency_ShouldDecreaseParallelism()
    {
        var ap = new AdaptiveParallelism(min: 2, max: 32);
        
        // Stabilize
        for (int i = 0; i < 100; i++) ap.Update(5.0, 0);
        int initial = ap.Current;
        
        // High latency
        for (int i = 0; i < 100; i++) ap.Update(500.0, 50);
        
        // High latency (> target 10ms) should DECREASE parallelism
        ap.Current.Should().BeLessThan(initial);
    }

    [Fact]
    public void LowLatency_ShouldIncreaseParallelism()
    {
        var ap = new AdaptiveParallelism(min: 2, max: 32);
        
        // Stabilize at high latency first
        for (int i = 0; i < 100; i++) ap.Update(500.0, 50);
        int initial = ap.Current;
        
        // Low latency
        for (int i = 0; i < 100; i++) ap.Update(5.0, 0);
        
        // Low latency (< target 10ms) should INCREASE parallelism
        ap.Current.Should().BeGreaterThan(initial);
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

    [Fact]
    public void Constructor_MinGreaterThanMax_ShouldSwapValues()
    {
        var ap = new AdaptiveParallelism(min: 10, max: 5);
        ap.Min.Should().Be(5);
        ap.Max.Should().Be(10);
        ap.Current.Should().BeGreaterThanOrEqualTo(5);
    }
}
