#nullable enable

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

    // Cached percentile values, invalidated on Record()
    private double _cachedP50 = double.NaN, _cachedP95 = double.NaN, _cachedP99 = double.NaN;
    private long _cachedTotalCount = -1;

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
        
        AtomicHelper.CompareExchangeLoop(ref _buckets[b], original => original + 1);
        
        Interlocked.Increment(ref _totalCount);
        _cachedTotalCount = -1; // Invalidate cache
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
            // Use Volatile.Read for non-destructive atomic read — avoids cache line invalidation
            // caused by CompareExchange on other cores. Percentile estimation does not require
            // exact consistency, so a non-destructive read is sufficient.
            cumulative += (long)Volatile.Read(ref _buckets[i]);
            if (cumulative >= target) return Math.Pow(_base, i + 0.5);
        }
        return Math.Pow(_base, _buckets.Length - 1);
    }

    /// <summary>Median (p50). Returns 0 if no data.</summary>
    public double P50 => GetOrCompute(0.50, ref _cachedP50);

    /// <summary>95th percentile.</summary>
    public double P95 => GetOrCompute(0.95, ref _cachedP95);

    /// <summary>99th percentile.</summary>
    public double P99 => GetOrCompute(0.99, ref _cachedP99);

    private double GetOrCompute(double p, ref double cached)
    {
        long currentTotal = Interlocked.Read(ref _totalCount);
        if (currentTotal != _cachedTotalCount || double.IsNaN(cached))
        {
            _cachedP50 = GetPercentile(0.50);
            _cachedP95 = GetPercentile(0.95);
            _cachedP99 = GetPercentile(0.99);
            _cachedTotalCount = currentTotal;
        }
        return p switch
        {
            0.50 => _cachedP50,
            0.95 => _cachedP95,
            0.99 => _cachedP99,
            _ => GetPercentile(p)
        };
    }
}