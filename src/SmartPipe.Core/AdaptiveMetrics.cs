using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Adaptive metrics with dynamic alpha. Uses α=0.2 for stable load, α=0.8 for spikes.</summary>
public class AdaptiveMetrics
{
    private double _emaLatencyMs, _emaThroughput;
    private long _lastUpdateTicks;

    public double SmoothLatencyMs => Volatile.Read(ref _emaLatencyMs);
    public double SmoothThroughputPerSec => Volatile.Read(ref _emaThroughput);

    public AdaptiveMetrics() => _lastUpdateTicks = Environment.TickCount64;

    public void Update(double latencyMs)
    {
        double oldLat = Volatile.Read(ref _emaLatencyMs);
        // Adaptive alpha: 0.8 for spike (>3x EMA), 0.2 for stable
        double alpha = (oldLat > 0.001 && latencyMs > oldLat * 3) ? 0.8 : 0.2;
        double newLat = oldLat < 0.001 ? latencyMs : alpha * latencyMs + (1.0 - alpha) * oldLat;
        Interlocked.Exchange(ref _emaLatencyMs, newLat);

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
}
