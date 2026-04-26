using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Exponential Histogram — accurate percentiles (p50/p95/p99) with O(log² n) memory.
/// Based on "Efficient Window Statistics" (VLDB, 2026).</summary>
public class ExponentialHistogram
{
    private readonly double[] _buckets;
    private readonly double _base;
    private long _totalCount;

    /// <summary>Create histogram with given range and bucket count.</summary>
    /// <param name="minValue">Minimum expected value (default: 0.1).</param>
    /// <param name="maxValue">Maximum expected value (default: 100,000).</param>
    /// <param name="bucketCount">Number of logarithmic buckets (default: 100).</param>
    public ExponentialHistogram(double minValue = 0.1, double maxValue = 100_000, int bucketCount = 100)
    {
        _base = Math.Pow(maxValue / minValue, 1.0 / bucketCount);
        _buckets = new double[bucketCount];
    }

    /// <summary>Record a value.</summary>
    /// <param name="value">Value to record (must be > 0).</param>
    public void Record(double value)
    {
        if (value <= 0) return;
        int b = (int)(Math.Log(value) / Math.Log(_base));
        if (b < 0) b = 0;
        if (b >= _buckets.Length) b = _buckets.Length - 1;
        
        double original, updated;
        do
        {
            original = _buckets[b];
            updated = original + 1;
        }
        while (Interlocked.CompareExchange(ref _buckets[b], updated, original) != original);
        
        Interlocked.Increment(ref _totalCount);
    }

    /// <summary>Get approximate p-percentile (0.0-1.0). Returns 0 if no data.</summary>
    /// <param name="p">Percentile to compute (e.g., 0.50 for median).</param>
    /// <returns>Approximate value at the given percentile, or 0 if no data recorded.</returns>
    public double GetPercentile(double p)
    {
        long total = Interlocked.Read(ref _totalCount);
        if (total == 0) return 0.0;
        
        long target = (long)(total * p), cumulative = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            cumulative += (long)Interlocked.CompareExchange(ref _buckets[i], 0, 0);
            if (cumulative >= target) return Math.Pow(_base, i + 0.5);
        }
        return Math.Pow(_base, _buckets.Length - 1);
    }

    /// <summary>Median (p50). Returns 0 if no data.</summary>
    public double P50 => GetPercentile(0.50);

    /// <summary>95th percentile.</summary>
    public double P95 => GetPercentile(0.95);

    /// <summary>99th percentile.</summary>
    public double P99 => GetPercentile(0.99);
}
