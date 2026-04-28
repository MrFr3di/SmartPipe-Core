using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>
/// Adaptive parallelism using discrete P-controller with dead zone and anti-windup.
/// Smoothly adjusts thread count based on latency error instead of binary thresholds.
/// </summary>
public class AdaptiveParallelism
{
    private readonly int _min, _max;
    private int _current;
    private double _avgLatencyMs = 10.0;
    private double _targetLatencyMs = 10.0;
    
    // P-controller parameters
    private const int DeadZone = 5;          // Ignore errors smaller than 5ms
    private const int ProportionalBand = 20; // Error of 20ms = 1 thread adjustment

    public int Current => Volatile.Read(ref _current);

    public AdaptiveParallelism(int min = 2, int max = 32)
    {
        _min = Math.Max(1, min);
        _max = Math.Min(Environment.ProcessorCount * 4, max);
        _current = Environment.ProcessorCount;
    }

    /// <summary>Update with current latency and queue size. Uses P-controller for smooth adjustments.</summary>
    public void Update(double currentLatencyMs, int queueSize)
    {
        const double alpha = 0.2;
        _avgLatencyMs = alpha * Math.Max(1, currentLatencyMs) + (1.0 - alpha) * _avgLatencyMs;
        _targetLatencyMs = alpha * Math.Max(1, currentLatencyMs) + (1.0 - alpha) * _targetLatencyMs;

        double error = _targetLatencyMs - _avgLatencyMs;

        // Dead zone: ignore small errors
        if (Math.Abs(error) < DeadZone) return;

        // Anti-windup: don't accumulate error when at limits
        if (_current >= _max && error > 0) return;
        if (_current <= _min && error < 0) return;

        // P-controller: error / proportionalBand = thread adjustment
        int adjustment = Math.Sign(error) * Math.Max(1, (int)(Math.Abs(error) / ProportionalBand));
        int newValue = Math.Clamp(_current + adjustment, _min, _max);
        Interlocked.Exchange(ref _current, newValue);
    }
}
