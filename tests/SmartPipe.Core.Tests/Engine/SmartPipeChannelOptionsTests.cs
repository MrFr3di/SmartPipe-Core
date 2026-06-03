using FluentAssertions;
using SmartPipe.Core;
using System.Threading.Channels;

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
        options.IsEnabled("ObjectPool").Should().BeFalse();
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

    [Fact]
    public void Constructor_ThrowsWhenUseRendezvousIsTrue()
    {
        var options = new SmartPipeChannelOptions { UseRendezvous = true };
        var ex = Assert.Throws<InvalidOperationException>(
            () => new SmartPipeChannel<object, object>(options));
        Assert.Contains("BoundedCapacity", ex.Message);
    }

    [Fact]
    public void Validate_ShouldAcceptDefaults()
    {
        var options = new SmartPipeChannelOptions();

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectInvalidMaxDegreeOfParallelism(int value)
    {
        var options = new SmartPipeChannelOptions { MaxDegreeOfParallelism = value };

        var act = () => options.Validate();

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(SmartPipeChannelOptions.MaxDegreeOfParallelism));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectInvalidBoundedCapacity(int value)
    {
        var options = new SmartPipeChannelOptions { BoundedCapacity = value };

        var act = () => options.Validate();

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(SmartPipeChannelOptions.BoundedCapacity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectInvalidTimeouts(int milliseconds)
    {
        var timeout = TimeSpan.FromMilliseconds(milliseconds);
        var options = new SmartPipeChannelOptions
        {
            AttemptTimeout = timeout,
            TotalRequestTimeout = timeout,
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ShouldRejectUndefinedFullMode()
    {
        var options = new SmartPipeChannelOptions
        {
            FullMode = (BoundedChannelFullMode)999,
        };

        var act = () => options.Validate();

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(SmartPipeChannelOptions.FullMode));
    }

    [Fact]
    public void Validate_ShouldRejectUndefinedRetryQueueOverflowPolicy()
    {
        var options = new SmartPipeChannelOptions
        {
            RetryQueueOverflowPolicy = (RetryQueueOverflowPolicy)999,
        };

        var act = () => options.Validate();

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(SmartPipeChannelOptions.RetryQueueOverflowPolicy));
    }

    [Fact]
    public void Constructor_ShouldValidateOptions()
    {
        var options = new SmartPipeChannelOptions { MaxDegreeOfParallelism = 0 };

        var act = () => new SmartPipeChannel<object, object>(options);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(SmartPipeChannelOptions.MaxDegreeOfParallelism));
    }
}
