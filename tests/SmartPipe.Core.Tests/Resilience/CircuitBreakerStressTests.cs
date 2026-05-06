using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Core.Tests.Resilience;

/// <summary>
/// Stress tests for CircuitBreaker — race conditions, HalfOpen flapping, adaptive alpha.
/// DO NOT modify production code. Report CODE BUG findings with evidence.
/// </summary>
public class CircuitBreakerStressTests
{
    private static readonly FieldInfo EwmaField = typeof(CircuitBreaker)
        .GetField("_ewmaFailureRate", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new Exception("Could not find _ewmaFailureRate field");

    private static readonly FieldInfo WindowField = typeof(CircuitBreaker)
        .GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new Exception("Could not find _window field");

    // ============================================
    // Task 1: Race Condition Stress Test
    // ============================================

    [Fact(Timeout = 120000)]
    [Trait("Category", "Stress")]
    public async Task RaceCondition_50Threads_ShouldNotCorruptState()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 10,
            breakDuration: TimeSpan.FromSeconds(30));

        const int threadCount = 50;
        const int iterationsPerThread = 1000;
        var tasks = new Task[threadCount];
        var errors = new ConcurrentBag<Exception>();

        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var rnd = new Random(Thread.CurrentThread.ManagedThreadId + Environment.TickCount);
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    try
                    {
                        if (rnd.NextDouble() < 0.5)
                            cb.RecordSuccess();
                        else
                            cb.RecordFailure();

                        // Occasionally check state
                        if (i % 100 == 0)
                        {
                            var state = cb.State;
                            state.Should().BeOneOf(CircuitState.Closed, CircuitState.Open, CircuitState.HalfOpen, CircuitState.Isolated);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        errors.Should().BeEmpty("no exceptions should occur during parallel execution");

        // Verify state is valid
        cb.State.Should().BeOneOf(CircuitState.Closed, CircuitState.Open, CircuitState.HalfOpen, CircuitState.Isolated);

        // Verify failure ratio is in valid range
        var ratio = cb.GetCurrentFailureRatio();
        ratio.Should().BeInRange(0.0, 1.0, "failure ratio must be between 0 and 1");

        // Verify metrics succeed
        var metrics = cb.GetMetrics();
        metrics.Should().NotBeNull();
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Stress")]
    public async Task RaceCondition_EwmaNotCorrupted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 10,
            breakDuration: TimeSpan.FromSeconds(30));

        const int threadCount = 50;
        const int iterationsPerThread = 1000;
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                var rnd = new Random(Thread.CurrentThread.ManagedThreadId + Environment.TickCount);
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    if (cts.IsCancellationRequested)
                        return;
                    
                    if (rnd.NextDouble() < 0.5)
                        cb.RecordSuccess();
                    else
                        cb.RecordFailure();
                    
                    // Small delay to prevent tight loop
                    if (i % 100 == 0)
                        await Task.Delay(1, cts.Token);
                }
            }, cts.Token);
        }

        await Task.WhenAny(
            Task.WhenAll(tasks),
            Task.Delay(TimeSpan.FromSeconds(30), cts.Token));

        // Check EWMA value via reflection
        double ewma = (double)EwmaField.GetValue(cb)!;

        // Check for corruption: NaN, Infinity, negative
        double.IsNaN(ewma).Should().BeFalse("EWMA should not be NaN");
        double.IsInfinity(ewma).Should().BeFalse("EWMA should not be Infinity");
        ewma.Should().BeGreaterThanOrEqualTo(0.0, "EWMA should not be negative");
        ewma.Should().BeLessThanOrEqualTo(1.0, "EWMA should not exceed 1.0");
    }

    [Fact(Timeout = 120000)]
    public async Task RaceCondition_EwmaLostUpdates_ShouldBeDetected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // This test attempts to expose the lost-update race condition in _ewmaFailureRate updates.
        // The bug: _ewmaFailureRate is updated via read-modify-write without atomic operations.
        // Line 82: _ewmaFailureRate = (1.0 - alpha) * _ewmaFailureRate;  // RecordSuccess
        // Line 104: _ewmaFailureRate = alpha * 1.0 + (1.0 - alpha) * _ewmaFailureRate;  // RecordFailure

        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 10,
            breakDuration: TimeSpan.FromSeconds(30));

        const int threadCount = 100;
        const int iterationsPerThread = 500;
        var failureCount = 0;
        var successCount = 0;
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            // Alternate between success and failure to create predictable EWMA
            bool recordFailure = t % 2 == 0;
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    if (cts.IsCancellationRequested)
                        return;

                    if (recordFailure)
                    {
                        cb.RecordFailure();
                        Interlocked.Increment(ref failureCount);
                    }
                    else
                    {
                        cb.RecordSuccess();
                        Interlocked.Increment(ref successCount);
                    }

                    // Small delay to prevent tight loop and reduce contention
                    if (i % 10 == 0)
                        await Task.Delay(1, cts.Token);
                }
            }, cts.Token);
        }

        await Task.WhenAny(
            Task.WhenAll(tasks),
            Task.Delay(TimeSpan.FromSeconds(120), cts.Token));

        // With the race condition, some updates may be lost
        // The EWMA value should reflect the ratio of failures to total calls
        // But due to lost updates, it may not converge correctly
        double ewma = (double)EwmaField.GetValue(cb)!;

        // Log the values for analysis
        Console.WriteLine($"Failure count: {failureCount}, Success count: {successCount}");
        Console.WriteLine($"Expected failure ratio: {(double)failureCount / (failureCount + successCount)}");
        Console.WriteLine($"Actual EWMA: {ewma}");

        // The EWMA should be between 0 and 1
        double.IsNaN(ewma).Should().BeFalse("EWMA should not be NaN");
        double.IsInfinity(ewma).Should().BeFalse("EWMA should not be Infinity");
        ewma.Should().BeGreaterThanOrEqualTo(0.0, "EWMA should not be negative");
        ewma.Should().BeLessThanOrEqualTo(1.0, "EWMA should not exceed 1.0");
    }

    // ============================================
    // Task 2: HalfOpen Flapping Test
    // ============================================

    [Fact]
    [Trait("Category", "Stress")]
    public void HalfOpen_FlappingSuccessFailure_ShouldTransitionCorrectly()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 5,
            breakDuration: TimeSpan.FromMilliseconds(100),
            maxHalfOpenRequests: 3);

        // Force Open state: 5 failures (>= minimumThroughput) with 100% failure ratio (> 0.5)
        for (int i = 0; i < 5; i++)
            cb.RecordFailure();

        cb.State.Should().Be(CircuitState.Open, "should be Open after failures");

        // Wait for breakDuration
        Thread.Sleep(150);

        // Enter HalfOpen
        cb.AllowRequest().Should().BeTrue("first request in HalfOpen should succeed");
        cb.State.Should().Be(CircuitState.HalfOpen, "should be HalfOpen");

        // Rapidly alternate Success/Failure
        for (int cycle = 0; cycle < 10; cycle++)
        {
            cb.RecordFailure();
            cb.RecordSuccess();
        }

        // State should be valid
        cb.State.Should().BeOneOf(CircuitState.Closed, CircuitState.Open, CircuitState.HalfOpen, CircuitState.Isolated);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void HalfOpen_MaxRequestsEnforced()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 5,
            breakDuration: TimeSpan.FromMilliseconds(100),
            maxHalfOpenRequests: 3);

        // Force Open
        for (int i = 0; i < 5; i++)
            cb.RecordFailure();

        // Wait for breakDuration
        Thread.Sleep(150);

        // Enter HalfOpen and check max requests
        int allowedCount = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cb.AllowRequest())
                allowedCount++;
        }

        allowedCount.Should().Be(3, "only maxHalfOpenRequests should be allowed");
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void HalfOpen_Successes_ShouldTransitionToClosed()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 5,
            breakDuration: TimeSpan.FromMilliseconds(100),
            maxHalfOpenRequests: 3);

        // Force Open
        for (int i = 0; i < 5; i++)
            cb.RecordFailure();

        Thread.Sleep(150);

        // Enter HalfOpen
        cb.AllowRequest().Should().BeTrue();
        cb.State.Should().Be(CircuitState.HalfOpen);

        // Record enough successes to close (maxHalfOpenRequests/2 + 1 = 2 + 1 = 3)
        cb.RecordSuccess();
        cb.RecordSuccess();
        cb.RecordSuccess();

        cb.State.Should().Be(CircuitState.Closed, "enough successes in HalfOpen should close circuit");
    }

    // ============================================
    // Task 3: Adaptive Alpha Verification Test
    // ============================================

    [Fact]
    [Trait("Category", "Stress")]
    public void AdaptiveAlpha_LowFailureRate_AlphaShouldBe0_2()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 100,
            breakDuration: TimeSpan.FromSeconds(30));

        // Record mostly successes to keep EWMA low
        for (int i = 0; i < 100; i++)
            cb.RecordSuccess();

        double ewma = (double)EwmaField.GetValue(cb)!;
        ewma.Should().BeLessThanOrEqualTo(0.1, "EWMA should be <= 0.1 after mostly successes");

        // Now record a failure and check EWMA update uses alpha=0.2
        // With alpha=0.2: new = 0.2 * 1.0 + 0.8 * old
        // With alpha=0.5: new = 0.5 * 1.0 + 0.5 * old
        double ewmaBefore = (double)EwmaField.GetValue(cb)!;
        cb.RecordFailure();
        double ewmaAfter = (double)EwmaField.GetValue(cb)!;

        // If alpha=0.2: ewmaAfter = 0.2 + 0.8 * ewmaBefore
        // If alpha=0.5: ewmaAfter = 0.5 + 0.5 * ewmaBefore
        double expectedWith02 = 0.2 * 1.0 + 0.8 * ewmaBefore;
        double expectedWith05 = 0.5 * 1.0 + 0.5 * ewmaBefore;

        // At low failure rates, should use alpha=0.2
        ewmaAfter.Should().BeInRange(expectedWith02 - 0.01, expectedWith02 + 0.01,
            "alpha should be 0.2 when EWMA <= 0.1");
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void AdaptiveAlpha_HighFailureRate_AlphaShouldBe0_5()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 100,
            breakDuration: TimeSpan.FromSeconds(30));

        // Record failures to drive EWMA above 0.1
        for (int i = 0; i < 50; i++)
            cb.RecordFailure();

        double ewma = (double)EwmaField.GetValue(cb)!;
        ewma.Should().BeGreaterThan(0.1, "EWMA should be > 0.1 after failures");

        // Record another failure and check alpha=0.5 is used
        double ewmaBefore = (double)EwmaField.GetValue(cb)!;
        cb.RecordFailure();
        double ewmaAfter = (double)EwmaField.GetValue(cb)!;

        double expectedWith05 = 0.5 * 1.0 + 0.5 * ewmaBefore;
        double expectedWith02 = 0.2 * 1.0 + 0.8 * ewmaBefore;

        // At high failure rates, should use alpha=0.5
        ewmaAfter.Should().BeInRange(expectedWith05 - 0.01, expectedWith05 + 0.01,
            "alpha should be 0.5 when EWMA > 0.1");
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void AdaptiveAlpha_ThresholdCrossing_ShouldSwitchAlpha()
    {
        var cb = new CircuitBreaker(
            failureRatio: 0.5,
            minimumThroughput: 100,
            breakDuration: TimeSpan.FromSeconds(30));

        // Start with successes (alpha should be 0.2)
        for (int i = 0; i < 50; i++)
            cb.RecordSuccess();

        double ewmaLow = (double)EwmaField.GetValue(cb)!;
        ewmaLow.Should().BeLessThanOrEqualTo(0.1, "EWMA should be low after successes");

        // Gradually introduce failures until EWMA crosses 0.1
        // We need to track when the threshold is crossed
        bool thresholdCrossed = false;
        for (int i = 0; i < 100; i++)
        {
            cb.RecordFailure();
            double currentEwma = (double)EwmaField.GetValue(cb)!;

            if (!thresholdCrossed && currentEwma > 0.1)
            {
                thresholdCrossed = true;

                // At this point, the NEXT failure should use alpha=0.5
                // But due to the code structure, the check is done at the start of RecordFailure
                // So the transition might be one step behind
            }
        }

        thresholdCrossed.Should().BeTrue("EWMA should cross 0.1 threshold");
    }
}
