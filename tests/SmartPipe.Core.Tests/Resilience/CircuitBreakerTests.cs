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
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void AboveThreshold_ShouldOpen()
    {
        var cb = new CircuitBreaker(minimumThroughput: 5, failureRatio: 0.5);
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void HalfOpen_ShouldLimitRequests()
    {
        var cb = new CircuitBreaker(failureRatio: 0.5, minimumThroughput: 5, breakDuration: TimeSpan.FromMilliseconds(10), maxHalfOpenRequests: 2);
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(15);
        cb.AllowRequest().Should().BeTrue();
        cb.AllowRequest().Should().BeTrue();
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void HalfOpen_WithSuccesses_ShouldClose()
    {
        var cb = new CircuitBreaker(failureRatio: 0.5, minimumThroughput: 5, breakDuration: TimeSpan.FromMilliseconds(10), maxHalfOpenRequests: 3);
        for (int i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(15);
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
    public void GetMetrics_ShouldReturnDictionary()
    {
        var cb = new CircuitBreaker();
        var metrics = cb.GetMetrics();
        metrics.Should().ContainKey("cb_state");
        metrics.Should().ContainKey("cb_failure_ratio");
        metrics.Should().ContainKey("cb_ewma_failure_rate");
    }
}
