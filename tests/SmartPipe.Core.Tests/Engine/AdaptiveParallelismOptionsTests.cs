#nullable enable

using System.Threading.Channels;
using FluentAssertions;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveParallelismOptionsTests
{
    [Fact]
    public void DefaultOptions_ShouldBeDisabled()
    {
        var options = new AdaptiveParallelismOptions();

        options.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectMinDegreeLessThanOne(int minDegreeOfParallelism)
    {
        var options = ValidEnabledOptions();
        options.MinDegreeOfParallelism = minDegreeOfParallelism;

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.MinDegreeOfParallelism));
    }

    [Fact]
    public void Validate_ShouldRejectMaxDegreeLessThanMinDegree()
    {
        var options = ValidEnabledOptions();
        options.MinDegreeOfParallelism = 4;
        options.MaxDegreeOfParallelism = 3;

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.MaxDegreeOfParallelism));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Validate_ShouldRejectInitialDegreeOutsideMinMaxRange(int initialDegreeOfParallelism)
    {
        var options = ValidEnabledOptions();
        options.MinDegreeOfParallelism = 1;
        options.MaxDegreeOfParallelism = 4;
        options.InitialDegreeOfParallelism = initialDegreeOfParallelism;

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.InitialDegreeOfParallelism));
    }

    [Fact]
    public void Validate_ShouldRejectInitialInFlightBelowInitialDegree()
    {
        var options = ValidEnabledOptions();
        options.InitialDegreeOfParallelism = 3;
        options.InitialInFlightItems = 2;

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.InitialInFlightItems));
    }

    [Fact]
    public void Validate_ShouldRejectMaxInFlightBelowInitialInFlight()
    {
        var options = ValidEnabledOptions();
        options.InitialInFlightItems = 4;
        options.MaxInFlightItems = 3;

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.MaxInFlightItems));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectNonPositiveSamplingInterval(int milliseconds)
    {
        var options = ValidEnabledOptions();
        options.SamplingInterval = TimeSpan.FromMilliseconds(milliseconds);

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.SamplingInterval));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectNonPositiveCooldown(int milliseconds)
    {
        var options = ValidEnabledOptions();
        options.Cooldown = TimeSpan.FromMilliseconds(milliseconds);

        var act = () => options.Validate(BoundedChannelFullMode.Wait, jumpHashEnabled: false);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(AdaptiveParallelismOptions.Cooldown));
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    public void SmartPipeChannelOptionsValidate_ShouldRejectAdaptiveModeWhenFullModeIsNotWait(
        BoundedChannelFullMode fullMode)
    {
        var options = new SmartPipeChannelOptions { FullMode = fullMode };
        options.AdaptiveParallelism.Enabled = true;

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Adaptive parallelism*FullMode*Wait*");
    }

    [Fact]
    public void SmartPipeChannelOptionsValidate_ShouldRejectAdaptiveModeWhenJumpHashIsEnabled()
    {
        var options = new SmartPipeChannelOptions();
        options.AdaptiveParallelism.Enabled = true;
        options.EnableFeature("JumpHash");

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Adaptive parallelism*JumpHash*");
    }

    private static AdaptiveParallelismOptions ValidEnabledOptions()
    {
        return new AdaptiveParallelismOptions
        {
            Enabled = true,
            MinDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 4,
            InitialDegreeOfParallelism = 2,
            InitialInFlightItems = 2,
            MaxInFlightItems = 8,
            SamplingInterval = TimeSpan.FromMilliseconds(100),
            Cooldown = TimeSpan.FromMilliseconds(500),
        };
    }
}
