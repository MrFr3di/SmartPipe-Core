#nullable enable

using System;
using System.Diagnostics;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>
/// Adaptive metrics with Double EMA (level + velocity) and one-step prediction.
/// Tracks smoothed latency, throughput, and rate of change for proactive control.
/// Uses Stopwatch.GetTimestamp() for reliable time measurement immune to system clock changes.
/// </summary>
public class AdaptiveMetrics
{
    private double _emaLatencyMs,
        _emaThroughput,
        _emaVelocity;
    private double _prevEmaLatencyMs;
    private long _lastTimestamp;

    /// <summary>Smoothed latency via EMA.</summary>
    public double SmoothLatencyMs => Volatile.Read(ref _emaLatencyMs);

    /// <summary>Smoothed throughput (items/sec) via EMA.</summary>
    public double SmoothThroughputPerSec => Volatile.Read(ref _emaThroughput);

    /// <summary>Rate of latency change (velocity) via Double EMA.</summary>
    public double LatencyVelocity => Volatile.Read(ref _emaVelocity);

    /// <summary>Initialize adaptive metrics with current timestamp.</summary>
    public AdaptiveMetrics() => _lastTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Update metrics with a new latency sample.</summary>
    /// <param name="latencyMs">Measured latency in milliseconds.</param>
    public void Update(double latencyMs)
    {
        double oldLat = Volatile.Read(ref _emaLatencyMs);
        double alpha = (oldLat > 0.001 && latencyMs > oldLat * 3) ? 0.8 : 0.2;

        // Level EMA
        double newLat = oldLat < 0.001 ? latencyMs : alpha * latencyMs + (1.0 - alpha) * oldLat;
        Interlocked.Exchange(ref _emaLatencyMs, newLat);

        // Velocity EMA (Double EMA)
        double oldPrev = _prevEmaLatencyMs;
        _prevEmaLatencyMs = newLat;
        double instantVelocity = newLat - oldPrev;
        double beta = 0.1;
        double oldVel = Volatile.Read(ref _emaVelocity);
        double newVel =
            oldVel < 0.001 ? instantVelocity : beta * instantVelocity + (1.0 - beta) * oldVel;
        Interlocked.Exchange(ref _emaVelocity, newVel);

        // Throughput EMA using Stopwatch for reliable time measurement
        long now = Stopwatch.GetTimestamp();
        long lastTs = Interlocked.Exchange(ref _lastTimestamp, now);
        double elapsedSec = (now - lastTs) / (double)Stopwatch.Frequency;
        if (elapsedSec > 0.0 && elapsedSec < 3600.0) // Guard against abnormal values (wraparound, first call)
        {
            double instantTp = 1.0 / elapsedSec;
            double oldTp = Volatile.Read(ref _emaThroughput);
            double newTp = oldTp < 0.001 ? instantTp : 0.2 * instantTp + 0.8 * oldTp;
            Interlocked.Exchange(ref _emaThroughput, newTp);
        }
    }

    /// <summary>Predict latency one step ahead using level + velocity.</summary>
    /// <returns>Predicted latency in milliseconds, non-negative.</returns>
    public double PredictNextLatency()
    {
        return Math.Max(0, Volatile.Read(ref _emaLatencyMs) + Volatile.Read(ref _emaVelocity));
    }
}
