#nullable enable

using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>
/// Adaptive metrics with Double EMA (level + velocity) and one-step prediction.
/// Tracks smoothed latency, throughput, and rate of change for proactive control.
/// </summary>
public class AdaptiveMetrics
{
    private double _emaLatencyMs, _emaThroughput, _emaVelocity;
    private double _prevEmaLatencyMs;
    private long _lastUpdateTicks;

    /// <summary>Smoothed latency via EMA.</summary>
    public double SmoothLatencyMs => Volatile.Read(ref _emaLatencyMs);

    /// <summary>Smoothed throughput (items/sec) via EMA.</summary>
    public double SmoothThroughputPerSec => Volatile.Read(ref _emaThroughput);

    /// <summary>Rate of latency change (velocity) via Double EMA.</summary>
    public double LatencyVelocity => Volatile.Read(ref _emaVelocity);

    /// <summary>Initialize adaptive metrics with current tick count.</summary>
    public AdaptiveMetrics() => _lastUpdateTicks = Environment.TickCount64;

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
        double newVel = oldVel < 0.001 ? instantVelocity : beta * instantVelocity + (1.0 - beta) * oldVel;
        Interlocked.Exchange(ref _emaVelocity, newVel);

        // Throughput EMA
        var now = Environment.TickCount64;
        long lastTicks = Interlocked.Exchange(ref _lastUpdateTicks, now);
        double elapsedSec = (now - lastTicks) / 1000.0;
        if (elapsedSec > 0.0)
        {
            double instantTp = 1.0 / elapsedSec;
            double oldTp = Volatile.Read(ref _emaThroughput);
            double newTp = oldTp < 0.001 ? instantTp : 0.2 * instantTp + 0.8 * oldTp;
            Interlocked.Exchange(ref _emaThroughput, newTp);
        }
    }

    /// <summary>Predict latency one step ahead using level + velocity.</summary>
    public double PredictNextLatency()
    {
        return Math.Max(0, Volatile.Read(ref _emaLatencyMs) + Volatile.Read(ref _emaVelocity));
    }
}
