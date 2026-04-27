using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SmartPipe.Core;

public enum CircuitState { Closed, Open, HalfOpen, Isolated }

public class CircuitBreaker
{
    private readonly double _failureRatio;
    private readonly TimeSpan _samplingDuration;
    private readonly int _minimumThroughput;
    private readonly TimeSpan _breakDuration;
    private readonly int _maxHalfOpenRequests;

    private int _state = (int)CircuitState.Closed;
    private int _halfOpenCount;
    private int _halfOpenSuccesses;
    private long _openedAtTicks;
    private long _halfOpenAtTicks;

    private readonly ConcurrentQueue<(DateTime Timestamp, bool IsSuccess)> _window = new();

    public CircuitState State => (CircuitState)Volatile.Read(ref _state);

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

    public bool AllowRequest()
    {
        CleanupWindow();
        int currentState = Volatile.Read(ref _state);

        if (currentState == (int)CircuitState.Closed) return true;

        if (currentState == (int)CircuitState.Open)
        {
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _openedAtTicks) >= _breakDuration.Ticks)
            {
                Interlocked.Exchange(ref _state, (int)CircuitState.HalfOpen);
                Interlocked.Exchange(ref _halfOpenCount, 0);
                Interlocked.Exchange(ref _halfOpenSuccesses, 0);
                Interlocked.Exchange(ref _halfOpenAtTicks, DateTime.UtcNow.Ticks);
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

    public void RecordSuccess()
    {
        _window.Enqueue((DateTime.UtcNow, true));
        CleanupWindow();

        // Track successes in HalfOpen to close circuit
        if (Volatile.Read(ref _state) == (int)CircuitState.HalfOpen)
        {
            int successes = Interlocked.Increment(ref _halfOpenSuccesses);
            if (successes >= _maxHalfOpenRequests / 2 + 1)
            {
                Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
                Interlocked.Exchange(ref _halfOpenCount, 0);
                Interlocked.Exchange(ref _halfOpenSuccesses, 0);
            }
        }
    }

    public void RecordFailure()
    {
        _window.Enqueue((DateTime.UtcNow, false));
        CleanupWindow();

        int total = _window.Count;
        if (total < _minimumThroughput) return;

        int failures = 0;
        foreach (var (_, ok) in _window)
            if (!ok) failures++;

        int currentState = Volatile.Read(ref _state);
        if ((double)failures / total >= _failureRatio)
        {
            if (currentState == (int)CircuitState.Closed || currentState == (int)CircuitState.HalfOpen)
            {
                Interlocked.Exchange(ref _state, (int)CircuitState.Open);
                Interlocked.Exchange(ref _openedAtTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _halfOpenCount, 0);
                Interlocked.Exchange(ref _halfOpenSuccesses, 0);
            }
        }
    }

    public void Isolate() => Interlocked.Exchange(ref _state, (int)CircuitState.Isolated);

    public void Reset()
    {
        while (_window.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
        Interlocked.Exchange(ref _halfOpenCount, 0);
        Interlocked.Exchange(ref _halfOpenSuccesses, 0);
    }

    public double GetCurrentFailureRatio()
    {
        CleanupWindow();
        int total = _window.Count;
        if (total == 0) return 0;
        int failures = 0;
        foreach (var (_, ok) in _window) if (!ok) failures++;
        return (double)failures / total;
    }

    private void CleanupWindow()
    {
        var cutoff = DateTime.UtcNow - _samplingDuration;
        while (_window.TryPeek(out var item) && item.Timestamp < cutoff)
            _window.TryDequeue(out _);
    }
}
