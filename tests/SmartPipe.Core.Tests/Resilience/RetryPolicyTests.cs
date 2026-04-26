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
        policy.Strategy.Should().Be(BackoffStrategy.Exponential);
        policy.OnRetry.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithCustomValues_ShouldSetProperties()
    {
        var policy = new RetryPolicy(maxRetries: 5, delay: TimeSpan.FromMilliseconds(500));
        policy.MaxRetries.Should().Be(5);
        policy.Delay.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Constructor_WithZeroMaxRetries_ShouldThrow()
    {
        Action act = () => new RetryPolicy(maxRetries: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
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
    public void OnRetry_ShouldBeInvoked()
    {
        int callCount = 0;
        var policy = new RetryPolicy(
            onRetry: (ctx, err, count) => callCount++);
        policy.OnRetry!(null!, default, 1);
        callCount.Should().Be(1);
    }
}
