#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>
/// P-controller based backpressure. Smoothly adjusts delay proportional to queue fill error.
/// </summary>
public class BackpressureStrategy
{
    private const double DefaultTargetFillRatio = 0.70;
    private const double MinTargetFillRatio = 0.30;
    private const double HighFillRatioThreshold = 0.85;
    private const double MediumFillRatioThreshold = 0.50;
    private const int HighThroughputThreshold = 1000;
    private const int LowThroughputThreshold = 100;
    private const int LatencyThresholdMs = 50;
    private const double LatencyAdjustment = 0.10;
    private const double KpGain = 1.0;
    private const int MaxDelayMs = 200;
    private const int MinDelayMs = 0;
    private const int DelayScaleFactor = 100;

    private readonly int _capacity;
    private double _targetFillRatio = DefaultTargetFillRatio;

    /// <summary>Initialize backpressure strategy with maximum channel capacity.</summary>
    /// <param name="capacity">Maximum number of items in the channel.</param>
    public BackpressureStrategy(int capacity) => _capacity = capacity;

    /// <summary>Adjust target fill ratio based on current throughput and latency.</summary>
    /// <param name="throughputPerSec">Current pipeline throughput (items per second).</param>
    /// <param name="predictedLatencyMs">Predicted latency for next operations (optional).</param>
    public void UpdateThroughput(double throughputPerSec, double predictedLatencyMs = 0)
    {
        if (throughputPerSec > HighThroughputThreshold) _targetFillRatio = MediumFillRatioThreshold;
        else if (throughputPerSec < LowThroughputThreshold) _targetFillRatio = HighFillRatioThreshold;
        else _targetFillRatio = DefaultTargetFillRatio;

        if (predictedLatencyMs > LatencyThresholdMs) _targetFillRatio = Math.Max(MinTargetFillRatio, _targetFillRatio - LatencyAdjustment);
    }

    /// <summary>Calculate current channel fill ratio.</summary>
    /// <param name="currentSize">Current number of items in the channel.</param>
    /// <returns>Fill ratio between 0.0 (empty) and 1.0 (full).</returns>
    public double GetFillRatio(int currentSize) => (double)currentSize / _capacity;

    /// <summary>Apply throttling if channel fill exceeds target ratio.</summary>
    /// <param name="currentSize">Current number of items in the channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ValueTask completing after optional delay.</returns>
    public virtual async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
    {
        double fillRatio = GetFillRatio(currentSize);
        double error = fillRatio - _targetFillRatio;

        if (error <= 0) return; // Below target — no throttling

        double delayMs = KpGain * error * DelayScaleFactor;
        delayMs = Math.Max(MinDelayMs, Math.Min(delayMs, MaxDelayMs));
        if (delayMs > 1)
            await Task.Delay((int)delayMs, ct);
    }

}
