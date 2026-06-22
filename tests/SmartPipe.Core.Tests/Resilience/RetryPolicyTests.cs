using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

public class RetryPolicyTests
{
    [Fact]
    public void Constructor_ShouldSetDefaults()
    {
        var policy = new RetryPolicy();
        policy.MaxRetries.Should().Be(3);
        policy.Delay.Should().Be(TimeSpan.FromSeconds(1));
        policy.MaxDelay.Should().Be(TimeSpan.FromSeconds(30));
        policy.Strategy.Should().Be(BackoffStrategy.Exponential);
        policy.OnRetry.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithCustomValues_ShouldSetProperties()
    {
        var policy = new RetryPolicy(
            maxRetries: 5,
            delay: TimeSpan.FromMilliseconds(500),
            maxDelay: TimeSpan.FromSeconds(10),
            strategy: BackoffStrategy.Linear);

        policy.MaxRetries.Should().Be(5);
        policy.Delay.Should().Be(TimeSpan.FromMilliseconds(500));
        policy.MaxDelay.Should().Be(TimeSpan.FromSeconds(10));
        policy.Strategy.Should().Be(BackoffStrategy.Linear);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveMaxRetries_ShouldThrow(int maxRetries)
    {
        var act = () => new RetryPolicy(maxRetries: maxRetries);

        var assertion = act.Should().Throw<ArgumentOutOfRangeException>();
        assertion.Which.ParamName.Should().Be("maxRetries");
        assertion.Which.ActualValue.Should().Be(maxRetries);
    }

    public static TheoryData<TimeSpan> NonPositiveDelays() =>
        new()
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(-1),
        };

    [Theory]
    [MemberData(nameof(NonPositiveDelays))]
    public void Constructor_WithNonPositiveDelay_ShouldThrow(TimeSpan delay)
    {
        var act = () => new RetryPolicy(delay: delay);

        var assertion = act.Should().Throw<ArgumentOutOfRangeException>();
        assertion.Which.ParamName.Should().Be("delay");
        assertion.Which.ActualValue.Should().Be(delay);
    }

    [Theory]
    [MemberData(nameof(NonPositiveDelays))]
    public void Constructor_WithNonPositiveMaxDelay_ShouldThrow(TimeSpan maxDelay)
    {
        var act = () => new RetryPolicy(maxDelay: maxDelay);

        var assertion = act.Should().Throw<ArgumentOutOfRangeException>();
        assertion.Which.ParamName.Should().Be("maxDelay");
        assertion.Which.ActualValue.Should().Be(maxDelay);
    }

    [Fact]
    public void Constructor_WithMaxDelayLessThanDelay_ShouldThrow()
    {
        var maxDelay = TimeSpan.FromSeconds(1);

        var act = () => new RetryPolicy(
            delay: TimeSpan.FromSeconds(10),
            maxDelay: maxDelay);

        var assertion = act.Should().Throw<ArgumentOutOfRangeException>();
        assertion.Which.ParamName.Should().Be("maxDelay");
        assertion.Which.ActualValue.Should().Be(maxDelay);
    }

    [Fact]
    public void Constructor_WithDelayGreaterThanDefaultMaxDelay_ShouldThrow()
    {
        var act = () => new RetryPolicy(delay: TimeSpan.FromMinutes(1));

        var assertion = act.Should().Throw<ArgumentOutOfRangeException>();
        assertion.Which.ParamName.Should().Be("maxDelay");
        assertion.Which.ActualValue.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Constructor_WithMaxDelayEqualToDelay_ShouldBeAllowed()
    {
        var policy = new RetryPolicy(
            delay: TimeSpan.FromSeconds(5),
            maxDelay: TimeSpan.FromSeconds(5));

        policy.Delay.Should().Be(TimeSpan.FromSeconds(5));
        policy.MaxDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Constructor_WithInvalidBackoffStrategy_ShouldThrow()
    {
        var invalidStrategy = (BackoffStrategy)999;

        var act = () => new RetryPolicy(strategy: invalidStrategy);

        var assertion = act.Should().Throw<ArgumentOutOfRangeException>();
        assertion.Which.ParamName.Should().Be("strategy");
        assertion.Which.ActualValue.Should().Be(invalidStrategy);
    }

    [Fact]
    public void ShouldRetry_TransientError_ShouldReturnTrue()
    {
        var policy = new RetryPolicy();
        policy.ShouldRetry(new SmartPipeError("temp", ErrorType.Transient)).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetry_PermanentError_ShouldReturnFalse()
    {
        var policy = new RetryPolicy();
        policy.ShouldRetry(new SmartPipeError("perm", ErrorType.Permanent)).Should().BeFalse();
    }

    [Fact]
    public void ShouldRetry_WithCustomPredicate_ShouldUsePredicate()
    {
        var policy = new RetryPolicy(
            retryOn: error => error.Category == "Network");
        policy.ShouldRetry(new SmartPipeError("e", ErrorType.Permanent, "Network")).Should().BeTrue();
        policy.ShouldRetry(new SmartPipeError("e", ErrorType.Transient, "IO")).Should().BeFalse();
    }

    [Fact]
    public void GetDelay_Exponential_ShouldDouble()
    {
        var policy = new RetryPolicy(delay: TimeSpan.FromMilliseconds(100), strategy: BackoffStrategy.Exponential);
        policy.GetDelay(1).Should().Be(TimeSpan.FromMilliseconds(100));
        policy.GetDelay(2).Should().Be(TimeSpan.FromMilliseconds(200));
        policy.GetDelay(3).Should().Be(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void GetDelay_Fixed_ShouldBeConstant()
    {
        var policy = new RetryPolicy(delay: TimeSpan.FromMilliseconds(100), strategy: BackoffStrategy.Fixed);
        policy.GetDelay(1).Should().Be(TimeSpan.FromMilliseconds(100));
        policy.GetDelay(5).Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void GetDelay_Linear_ShouldScale()
    {
        var policy = new RetryPolicy(delay: TimeSpan.FromMilliseconds(100), strategy: BackoffStrategy.Linear);
        policy.GetDelay(1).Should().Be(TimeSpan.FromMilliseconds(100));
        policy.GetDelay(2).Should().Be(TimeSpan.FromMilliseconds(200));
        policy.GetDelay(3).Should().Be(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void GetDelay_ShouldCapAtMaxDelay()
    {
        var policy = new RetryPolicy(delay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(5), strategy: BackoffStrategy.Exponential);
        policy.GetDelay(10).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GetDelay_WithVeryLargeRetryCount_ShouldCapAtMaxDelay()
    {
        var policy = new RetryPolicy(
            delay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(30),
            strategy: BackoffStrategy.Exponential);

        policy.GetDelay(int.MaxValue).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetDelay_WithNonPositiveRetryCount_ShouldReturnZero(int retryCount)
    {
        var policy = new RetryPolicy();

        policy.GetDelay(retryCount).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void OnRetry_ShouldBeInvoked()
    {
        int callCount = 0;
        var policy = new RetryPolicy(
            onRetry: (ctx, err, count) => callCount++);
        policy.OnRetry!(null!, default, 1);
        callCount.Should().Be(1);
    }
}
