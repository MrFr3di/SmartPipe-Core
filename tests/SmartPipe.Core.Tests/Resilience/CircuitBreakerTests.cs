using System.Collections.Concurrent;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

public class CircuitBreakerTests
{
    [Fact]
    public void InitialState_ShouldBeClosed()
    {
        var cb = new CircuitBreaker();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void AllowRequest_WhenClosed_ShouldReturnTrue()
    {
        var cb = new CircuitBreaker();
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void BelowMinimumThroughput_ShouldNotOpen()
    {
        var cb = new CircuitBreaker(minimumThroughput: 10, failureRatio: 0.5);
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        cb.State.Should().Be(CircuitState.Closed); // Not enough requests
    }

    [Fact]
    public void AboveThreshold_ShouldOpen()
    {
        var cb = new CircuitBreaker(minimumThroughput: 5, failureRatio: 0.5);
        // Record 5 failures + 1 success = 83% failure rate > 50%
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void HalfOpen_ShouldLimitRequests()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 5,
            breakDuration: TimeSpan.FromMilliseconds(10),
            maxHalfOpenRequests: 2);

        // Open circuit
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(15);

        // First request allowed
        cb.AllowRequest().Should().BeTrue();
        // Second request allowed
        cb.AllowRequest().Should().BeTrue();
        // Third request blocked
        cb.AllowRequest().Should().BeFalse();
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void HalfOpen_WithSuccesses_ShouldClose()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 5,
            breakDuration: TimeSpan.FromMilliseconds(10),
            maxHalfOpenRequests: 3);

        // Open
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(15);

        // Half-open
        cb.AllowRequest();
        cb.RecordSuccess();
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Isolate_ShouldBlockAll()
    {
        var cb = new CircuitBreaker();
        cb.Isolate();
        cb.State.Should().Be(CircuitState.Isolated);
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void Reset_ShouldClearAndClose()
    {
        var cb = new CircuitBreaker(minimumThroughput: 5);
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        cb.State.Should().Be(CircuitState.Open);

        cb.Reset();
        cb.State.Should().Be(CircuitState.Closed);
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void GetCurrentFailureRatio_ShouldReflectWindow()
    {
        var cb = new CircuitBreaker(samplingDuration: TimeSpan.FromSeconds(60));
        cb.RecordFailure();
        cb.RecordSuccess();
        cb.RecordSuccess();

        cb.GetCurrentFailureRatio().Should().BeApproximately(0.33, 0.01);
    }

    [Fact]
    public void State_ShouldBeThreadSafe()
    {
        var cb = new CircuitBreaker(failureRatio: 0.5, minimumThroughput: 50);
        var states = new ConcurrentBag<CircuitState>();

        Parallel.For(0, 100, i =>
        {
            bool allowed = cb.AllowRequest();
            if (i % 3 == 0) cb.RecordFailure();
            else cb.RecordSuccess();
            states.Add(cb.State);
        });
    }
}
