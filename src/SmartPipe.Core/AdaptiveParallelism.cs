#nullable enable

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
    private const double AlphaScaleFactor = 50.0; // Scale factor for adaptive EMA alpha: larger latency deltas → faster adaptation

    /// <summary>Current parallelism level (thread count).</summary>
    public int Current => Volatile.Read(ref _current);

    /// <summary>Minimum allowed parallelism level.</summary>
    public int Min => _min;

    /// <summary>Maximum allowed parallelism level.</summary>
    public int Max => _max;

    /// <summary>Create P-controller with min/max bounds clamped to processor count.</summary>
    /// <param name="min">Minimum parallelism (default: 2).</param>
    /// <param name="max">Maximum parallelism, capped at ProcessorCount * 4 (default: 32).</param>
    public AdaptiveParallelism(int min = 2, int max = 32)
    {
        // Fix: swap if min > max to prevent ArgumentException in Math.Clamp
        if (min > max) (min, max) = (max, min);
        
        _min = Math.Max(1, min);
        _max = Math.Min(Environment.ProcessorCount * 4, max);
        _current = Math.Clamp(Environment.ProcessorCount, _min, _max);
    }

    /// <summary>Update with current latency and queue size. Uses P-controller for smooth adjustments.</summary>
    public void Update(double currentLatencyMs, int queueSize)
    {
        // Adaptive alpha: larger changes → faster response
        // This allows EMA to converge quickly when latency changes dramatically
        double currentAvg = Volatile.Read(ref _avgLatencyMs);
        double alpha = Math.Min(0.8, Math.Abs(currentLatencyMs - currentAvg) / AlphaScaleFactor);
        alpha = Math.Max(0.1, alpha); // Minimum alpha to ensure some smoothing
 
        Volatile.Write(ref _avgLatencyMs, alpha * Math.Max(1, currentLatencyMs) + (1.0 - alpha) * currentAvg);

        double error = _targetLatencyMs - currentLatencyMs;

        // Dead zone: ignore small errors
        if (Math.Abs(error) < DeadZone) return;

        // Anti-windup: don't accumulate error when at limits
        if (_current >= _max && error > 0) return;
        if (_current <= _min && error < 0) return;

        // P-controller: error / proportionalBand = thread adjustment
        // CAP the adjustment to prevent aggressive changes
        int rawAdjustment = (int)(Math.Abs(error) / ProportionalBand);
        int adjustment = Math.Sign(error) * Math.Max(1, Math.Min(3, rawAdjustment)); // Max 3 threads per iteration
        int newValue = Math.Clamp(_current + adjustment, _min, _max);
        Interlocked.Exchange(ref _current, newValue);
    }
}
