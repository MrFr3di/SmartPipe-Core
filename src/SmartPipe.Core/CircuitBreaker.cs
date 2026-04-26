using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Circuit Breaker states.</summary>
public enum CircuitState
{
    /// <summary>Normal operation: requests pass through.</summary>
    Closed,
    /// <summary>Failure threshold exceeded: requests are rejected.</summary>
    Open,
    /// <summary>Testing if the system has recovered: limited requests pass through.</summary>
    HalfOpen,
    /// <summary>Manually isolated: all requests are blocked.</summary>
    Isolated
}

/// <summary>Circuit Breaker pattern: Closed → Open → HalfOpen → Closed/Open.
/// Implements modern practices: sampling window, minimum throughput, dynamic break duration, manual control.</summary>
public class CircuitBreaker
{
    private readonly double _failureRatio;
    private readonly TimeSpan _samplingDuration;
    private readonly int _minimumThroughput;
    private readonly TimeSpan _breakDuration;
    private readonly int _maxHalfOpenRequests;
    private readonly object _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _halfOpenCount;
    private DateTime _openedAt;
    private DateTime _halfOpenAt;

    // Sliding window tracking
    private readonly Queue<DateTime> _successTimestamps = new();
    private readonly Queue<DateTime> _failureTimestamps = new();

    /// <summary>Current circuit state.</summary>
    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Create a circuit breaker with modern resilience settings.</summary>
    /// <param name="failureRatio">Ratio of failures to total requests that triggers opening (0.0-1.0). Default: 0.5.</param>
    /// <param name="samplingDuration">Time window for counting failures. Default: 30 seconds.</param>
    /// <param name="minimumThroughput">Minimum requests in window before circuit can open. Default: 10.</param>
    /// <param name="breakDuration">Time to stay open before trying half-open. Default: 30 seconds.</param>
    /// <param name="maxHalfOpenRequests">Maximum requests allowed in half-open state. Default: 3.</param>
    public CircuitBreaker(
        double failureRatio = 0.5,
        TimeSpan? samplingDuration = null,
        int minimumThroughput = 10,
        TimeSpan? breakDuration = null,
        int maxHalfOpenRequests = 3)
    {
        _failureRatio = failureRatio;
        _samplingDuration = samplingDuration ?? TimeSpan.FromSeconds(30);
        _minimumThroughput = minimumThroughput;
        _breakDuration = breakDuration ?? TimeSpan.FromSeconds(30);
        _maxHalfOpenRequests = maxHalfOpenRequests;
    }

    /// <summary>Check if a request is allowed through.</summary>
    public bool AllowRequest()
    {
        lock (_lock)
        {
            CleanupOldTimestamps();

            return _state switch
            {
                CircuitState.Closed => true,
                CircuitState.Open when DateTime.UtcNow - _openedAt >= _breakDuration =>
                    TransitionToHalfOpen(),
                CircuitState.Open => false,
                CircuitState.HalfOpen when _halfOpenCount < _maxHalfOpenRequests =>
                    (_halfOpenCount++, true).Item2,
                CircuitState.HalfOpen => false,
                CircuitState.Isolated => false,
                _ => false
            };
        }
    }

    /// <summary>Record a successful request.</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _successTimestamps.Enqueue(DateTime.UtcNow);

            if (_state == CircuitState.HalfOpen)
            {
                // Count successes in half-open; if enough, close the circuit
                int recentFailures = _failureTimestamps.Count(t => t > _halfOpenAt);
                int recentSuccesses = _successTimestamps.Count(t => t > _halfOpenAt);

                if (recentSuccesses >= _maxHalfOpenRequests / 2 + 1)
                {
                    _state = CircuitState.Closed;
                    _halfOpenCount = 0;
                }
            }

            CleanupOldTimestamps();
        }
    }

    /// <summary>Record a failed request.</summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureTimestamps.Enqueue(DateTime.UtcNow);
            CleanupOldTimestamps();

            int totalInWindow = _successTimestamps.Count + _failureTimestamps.Count;
            if (totalInWindow < _minimumThroughput) return;

            double failureRatio = (double)_failureTimestamps.Count / totalInWindow;
            if (failureRatio >= _failureRatio)
            {
                if (_state == CircuitState.Closed || _state == CircuitState.HalfOpen)
                {
                    _state = CircuitState.Open;
                    _openedAt = DateTime.UtcNow;
                    _halfOpenCount = 0;
                }
            }
        }
    }

    /// <summary>Manually isolate the circuit (block all requests).</summary>
    public void Isolate()
    {
        lock (_lock) _state = CircuitState.Isolated;
    }

    /// <summary>Manually close the circuit and reset all counters.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _successTimestamps.Clear();
            _failureTimestamps.Clear();
            _halfOpenCount = 0;
        }
    }

    /// <summary>Get current failure ratio in the sampling window.</summary>
    public double GetCurrentFailureRatio()
    {
        lock (_lock)
        {
            CleanupOldTimestamps();
            int total = _successTimestamps.Count + _failureTimestamps.Count;
            return total > 0 ? (double)_failureTimestamps.Count / total : 0;
        }
    }

    private bool TransitionToHalfOpen()
    {
        _state = CircuitState.HalfOpen;
        _halfOpenAt = DateTime.UtcNow;
        _halfOpenCount = 0;
        return true; // Allow the first request through
    }

    private void CleanupOldTimestamps()
    {
        var cutoff = DateTime.UtcNow - _samplingDuration;
        while (_successTimestamps.Count > 0 && _successTimestamps.Peek() < cutoff)
            _successTimestamps.Dequeue();
        while (_failureTimestamps.Count > 0 && _failureTimestamps.Peek() < cutoff)
            _failureTimestamps.Dequeue();
    }
}
