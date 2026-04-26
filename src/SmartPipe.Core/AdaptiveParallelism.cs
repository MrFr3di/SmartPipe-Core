using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Adaptive parallelism based on Little's Law: L = λ × W.
/// Automatically adjusts MaxDegreeOfParallelism based on observed latency and queue size.</summary>
public class AdaptiveParallelism
{
    private readonly int _min, _max;
    private double _avgLatencyMs;
    private int _current;

    /// <summary>Current recommended parallelism level.</summary>
    public int Current => Volatile.Read(ref _current);

    /// <summary>Create with parallelism range.</summary>
    /// <param name="min">Minimum parallelism (default: 2).</param>
    /// <param name="max">Maximum parallelism (default: 32).</param>
    public AdaptiveParallelism(int min = 2, int max = 32)
    {
        _min = Math.Max(1, min);
        _max = Math.Min(Environment.ProcessorCount * 4, max);
        _current = Environment.ProcessorCount;
        _avgLatencyMs = 10.0;
    }

    /// <summary>Update EMA latency and recalculate optimal parallelism.</summary>
    /// <param name="lastLatencyMs">Last measured latency in milliseconds.</param>
    /// <param name="queueSize">Current output queue size.</param>
    public void Update(double lastLatencyMs, int queueSize)
    {
        const double alpha = 0.2;
        _avgLatencyMs = alpha * Math.Max(1, lastLatencyMs) + (1.0 - alpha) * _avgLatencyMs;

        if (lastLatencyMs > _avgLatencyMs * 1.5 && queueSize > 10)
            Interlocked.Exchange(ref _current, Math.Min(_max, _current + 1));
        else if (lastLatencyMs < _avgLatencyMs * 0.5 && queueSize == 0)
            Interlocked.Exchange(ref _current, Math.Max(_min, _current - 1));
    }
}
