using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Adaptive metrics with exponential moving average (EMA, α=0.2).
/// Tracks smoothed latency and throughput without storing history.</summary>
public class AdaptiveMetrics
{
    private double _emaLatencyMs, _emaThroughput;
    private long _lastUpdateTicks;

    /// <summary>Smoothed latency in milliseconds (EMA).</summary>
    public double SmoothLatencyMs => Volatile.Read(ref _emaLatencyMs);

    /// <summary>Smoothed throughput in items per second (EMA).</summary>
    public double SmoothThroughputPerSec => Volatile.Read(ref _emaThroughput);

    /// <summary>Create with current time as baseline.</summary>
    public AdaptiveMetrics() => _lastUpdateTicks = Environment.TickCount64;

    /// <summary>Update EMA metrics with a new data point.</summary>
    /// <param name="latencyMs">Latency of the last processed item in milliseconds.</param>
    public void Update(double latencyMs)
    {
        const double alpha = 0.2;
        var now = Environment.TickCount64;

        // Update latency EMA
        double oldLat = Volatile.Read(ref _emaLatencyMs);
        double newLat = oldLat < 0.001 ? latencyMs : alpha * latencyMs + (1.0 - alpha) * oldLat;
        Interlocked.Exchange(ref _emaLatencyMs, newLat);

        // Update throughput EMA
        long lastTicks = Interlocked.Exchange(ref _lastUpdateTicks, now);
        double elapsedSec = (now - lastTicks) / 1000.0;
        if (elapsedSec > 0.0)
        {
            double instantTp = 1.0 / elapsedSec;
            double oldTp = Volatile.Read(ref _emaThroughput);
            double newTp = oldTp < 0.001 ? instantTp : alpha * instantTp + (1.0 - alpha) * oldTp;
            Interlocked.Exchange(ref _emaThroughput, newTp);
        }
    }
}
