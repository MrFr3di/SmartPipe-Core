using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

[Trait("Category", "ConcurrencyRegression")]
public sealed class HalfOpenProbeStateTests
{
    [Fact]
    public void TryEnter_BoundsActiveProbesAndExitNeverGoesNegative()
    {
        var probes = new HalfOpenProbeState();

        probes.TryEnter(2).Should().BeTrue();
        probes.TryEnter(2).Should().BeTrue();
        probes.TryEnter(2).Should().BeFalse();
        probes.Attempts.Should().Be(2);

        probes.Exit();
        probes.Exit();
        probes.Exit();

        probes.TryEnter(1).Should().BeTrue();
        probes.TryEnter(1).Should().BeFalse();
        probes.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task TryEnter_ConcurrentCallersNeverExceedLimit()
    {
        var probes = new HalfOpenProbeState();
        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (probes.TryEnter(5))
                Interlocked.Increment(ref admitted);
        })));

        admitted.Should().Be(5);
        probes.Attempts.Should().Be(5);
    }

    [Fact]
    public void StalePermit_ReleasesOnlyItsOwnGenerationSlot()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                MinimumThroughput = 1,
                BreakDuration = TimeSpan.FromSeconds(10),
                MaxHalfOpenRequests = 1,
            },
            time);
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));
        var stale = breaker.AcquirePermit();
        stale.IsAllowed.Should().BeTrue();

        breaker.Reset();
        breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(11));
        using var current = breaker.AcquirePermit();
        current.IsAllowed.Should().BeTrue("a new half-open generation starts with its own probe slots");

        stale.Dispose();

        breaker.AcquirePermit().IsAllowed.Should().BeFalse(
            "releasing a permit of an older generation must not free the current generation's slot");
        breaker.GetMetrics()["cb_half_open_attempts"].Should().Be(1);
    }
}
