#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Circuit breaker state machine states.</summary>
public enum CircuitState
{
    /// <summary>Normal operation, requests pass through.</summary>
    Closed,
    /// <summary>Circuit is open, requests are blocked.</summary>
    Open,
    /// <summary>Testing if the circuit can be closed.</summary>
    HalfOpen,
    /// <summary>Manually isolated, requests are blocked.</summary>
    Isolated
}

/// <summary>
/// Lock-free Circuit Breaker with hybrid failure detection:
/// EWMA for fast reaction + Sliding window for accurate threshold decisions.
/// </summary>
/// <remarks>
/// Uses lock-free atomic operations for state transitions.
/// EWMA provides early warning, sliding window makes final decisions.
/// </remarks>
public class CircuitBreaker
{
    private readonly double _failureRatio;
    private readonly TimeSpan _samplingDuration;
    private readonly int _minimumThroughput;
    private readonly TimeSpan _breakDuration;
    private readonly int _maxHalfOpenRequests;
    private readonly IClock _clock;

    private int _state = (int)CircuitState.Closed;
    private int _halfOpenCount;
    private int _halfOpenSuccesses;
    private long _openedAtTicks;
    private long _halfOpenAtTicks;

    // Hybrid: EWMA for early warning + Sliding window for decisions
    private double _ewmaFailureRate;
    private readonly ConcurrentQueue<(DateTime Timestamp, bool IsSuccess)> _window = new();

    /// <summary>Gets the current circuit state.</summary>
    public CircuitState State => (CircuitState)Volatile.Read(ref _state);

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    /// <param name="breakDuration">Duration to stay open before half-open.</param>
    /// <param name="maxHalfOpenRequests">Max requests in half-open state.</param>
    /// <param name="clock">Optional clock for testability (defaults to TimeProviderClock()).</param>
    public CircuitBreaker(
        double failureRatio = 0.5,
        TimeSpan? samplingDuration = null,
        int minimumThroughput = 10,
        TimeSpan? breakDuration = null,
        int maxHalfOpenRequests = 3,
        IClock? clock = null)
    {
        _failureRatio = failureRatio;
        _samplingDuration = samplingDuration ?? TimeSpan.FromSeconds(30);
        _minimumThroughput = minimumThroughput;
        _breakDuration = breakDuration ?? TimeSpan.FromSeconds(30);
        _maxHalfOpenRequests = maxHalfOpenRequests;
        _clock = clock ?? new TimeProviderClock();
    }

    /// <summary>Checks if a request is allowed through the circuit.</summary>
    /// <returns>True if request is allowed; false if circuit is open.</returns>
    /// <remarks>Transitions to HalfOpen after break duration expires.</remarks>
    public bool AllowRequest()
    {
        CleanupWindow();
        int currentState = Volatile.Read(ref _state);

        if (currentState == (int)CircuitState.Closed) return true;

        if (currentState == (int)CircuitState.Open)
        {
            if (_clock.UtcNow.Ticks - Interlocked.Read(ref _openedAtTicks) >= _breakDuration.Ticks)
            {
                Interlocked.Exchange(ref _state, (int)CircuitState.HalfOpen);
                Interlocked.Exchange(ref _halfOpenCount, 0);
                Interlocked.Exchange(ref _halfOpenSuccesses, 0);
                Interlocked.Exchange(ref _halfOpenAtTicks, _clock.UtcNow.Ticks);
                Interlocked.Increment(ref _halfOpenCount);
                return true;
            }
            return false;
        }

        if (currentState == (int)CircuitState.HalfOpen)
            return Interlocked.Increment(ref _halfOpenCount) <= _maxHalfOpenRequests;

        if (currentState == (int)CircuitState.Isolated) return false;
        return true;
    }

    /// <summary>Records a successful request and updates state.</summary>
    /// <remarks>May transition from HalfOpen to Closed on enough successes.</remarks>
    public void RecordSuccess()
    {
        _window.Enqueue((_clock.UtcNow, true));
        CleanupWindow();

        // EWMA update — atomic double update via CompareExchange loop
        double alpha = _ewmaFailureRate > 0.1 ? 0.5 : 0.2;
        AtomicHelper.CompareExchangeLoop(ref _ewmaFailureRate, current => (1.0 - alpha) * current);

        if (Volatile.Read(ref _state) == (int)CircuitState.HalfOpen)
        {
            int successes = Interlocked.Increment(ref _halfOpenSuccesses);
            if (successes >= _maxHalfOpenRequests / 2 + 1)
            {
                Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
                Interlocked.Exchange(ref _halfOpenCount, 0);
                Interlocked.Exchange(ref _halfOpenSuccesses, 0);
                Interlocked.Exchange(ref _ewmaFailureRate, 0.0);
            }
        }
    }

    /// <summary>Records a failed request and updates state.</summary>
    /// <remarks>May transition to Open if failure ratio exceeds threshold.</remarks>
    public void RecordFailure()
    {
        _window.Enqueue((_clock.UtcNow, false));
        CleanupWindow();
        UpdateEwmaFailureRate();
        AddEarlyWarningToWindow();
        EvaluateSlidingWindow();
    }

    private void UpdateEwmaFailureRate()
    {
        double alpha = _ewmaFailureRate > 0.1 ? 0.5 : 0.2;
        AtomicHelper.CompareExchangeLoop(ref _ewmaFailureRate, current => alpha * 1.0 + (1.0 - alpha) * current);
    }

    private void AddEarlyWarningToWindow()
    {
        // Early warning: EWMA spike → pre-emptively add to window
        if (_ewmaFailureRate > _failureRatio * 1.5)
            _window.Enqueue((_clock.UtcNow, false));
    }

    private void EvaluateSlidingWindow()
    {
        int total = _window.Count;
        if (total < _minimumThroughput) return;

        int failures = 0;
        foreach (var (_, ok) in _window) if (!ok) failures++;

        int currentState = Volatile.Read(ref _state);
        if ((double)failures / total >= _failureRatio)
            TransitionToOpenIfNeeded(currentState);
    }

    private void TransitionToOpenIfNeeded(int currentState)
    {
        if (currentState == (int)CircuitState.Closed || currentState == (int)CircuitState.HalfOpen)
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _openedAtTicks, _clock.UtcNow.Ticks);
            Interlocked.Exchange(ref _halfOpenCount, 0);
            Interlocked.Exchange(ref _halfOpenSuccesses, 0);
        }
    }

    /// <summary>Manually isolates the circuit (blocks all requests).</summary>
    public void Isolate() => Interlocked.Exchange(ref _state, (int)CircuitState.Isolated);

    /// <summary>Resets the circuit to Closed state and clears history.</summary>
    public void Reset()
    {
        while (_window.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
        Interlocked.Exchange(ref _halfOpenCount, 0);
        Interlocked.Exchange(ref _halfOpenSuccesses, 0);
        Interlocked.Exchange(ref _ewmaFailureRate, 0.0);
    }

    /// <summary>Calculates the current failure ratio from the sliding window.</summary>
    /// <returns>Failure ratio in range [0.0, 1.0].</returns>
    public double GetCurrentFailureRatio()
    {
        CleanupWindow();
        int total = _window.Count;
        if (total == 0) return 0;
        int failures = 0;
        foreach (var (_, ok) in _window) if (!ok) failures++;
        return (double)failures / total;
    }

    /// <summary>Export metrics for dashboard integration.</summary>
    /// <returns>Dictionary of circuit breaker metrics.</returns>
    public Dictionary<string, object> GetMetrics() => new()
    {
        ["cb_state"] = State.ToString(),
        ["cb_failure_ratio"] = GetCurrentFailureRatio(),
        ["cb_ewma_failure_rate"] = _ewmaFailureRate,
        ["cb_half_open_attempts"] = _halfOpenCount,
    };

    private void CleanupWindow()
    {
        var cutoff = _clock.UtcNow - _samplingDuration;
        while (_window.TryDequeue(out var item))
        {
            if (item.Timestamp >= cutoff)
            {
                // Item is not expired - re-enqueue it since we incorrectly removed it.
                // This handles the race condition where TryPeek+TryDequeue would
                // incorrectly remove a non-expired item.
                _window.Enqueue(item);
                break;
            }
            // Item was expired, continue to next item
        }
    }
}
