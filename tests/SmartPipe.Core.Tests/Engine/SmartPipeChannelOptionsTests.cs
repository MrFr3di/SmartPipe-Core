using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class SmartPipeChannelOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeSetCorrectly()
    {
        var options = new SmartPipeChannelOptions();

        options.MaxDegreeOfParallelism.Should().Be(Environment.ProcessorCount);
        options.BoundedCapacity.Should().Be(1000);
        options.ContinueOnError.Should().BeTrue();
        options.OnMetrics.Should().BeNull();
        options.DeduplicationFilter.Should().BeNull();
    }

    [Fact]
    public void FeatureFlags_DefaultValues()
    {
        var options = new SmartPipeChannelOptions();

        options.IsEnabled("RetryQueue").Should().BeFalse();
        options.IsEnabled("Metrics").Should().BeTrue();
        options.IsEnabled("CircuitBreaker").Should().BeFalse();
    }

    [Fact]
    public void EnableFeature_ShouldSetFlag()
    {
        var options = new SmartPipeChannelOptions();
        options.EnableFeature("RetryQueue");

        options.IsEnabled("RetryQueue").Should().BeTrue();
    }

    [Fact]
    public void DisableFeature_ShouldClearFlag()
    {
        var options = new SmartPipeChannelOptions();
        options.DisableFeature("Metrics");

        options.IsEnabled("Metrics").Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_UnknownFeature_ShouldBeFalse()
    {
        var options = new SmartPipeChannelOptions();
        options.IsEnabled("UnknownFeature").Should().BeFalse();
    }

    [Fact]
    public void DeduplicationFilter_ShouldBeSettable()
    {
        var filter = new DeduplicationFilter();
        var options = new SmartPipeChannelOptions { DeduplicationFilter = filter };

        options.DeduplicationFilter.Should().Be(filter);
    }
}
