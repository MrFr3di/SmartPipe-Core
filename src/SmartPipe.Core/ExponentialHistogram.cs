#nullable enable

using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Fixed-range logarithmic histogram with approximate percentiles.</summary>
public class ExponentialHistogram
{
    private readonly double _minValue;
    private readonly double _maxValue;
    private readonly double _base;
    private readonly double _logBase;
    private readonly long[] _buckets;
    private long _totalCount;

    /// <summary>Create histogram with given range and bucket count.</summary>
    /// <param name="minValue">Minimum expected value (default: 0.1).</param>
    /// <param name="maxValue">Maximum expected value (default: 100,000).</param>
    /// <param name="bucketCount">Number of logarithmic buckets (default: 100).</param>
    public ExponentialHistogram(
        double minValue = 0.1,
        double maxValue = 100_000,
        int bucketCount = 100
    )
    {
        if (!double.IsFinite(minValue) || minValue <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(minValue),
                "Minimum value must be finite and greater than zero."
            );
        if (!double.IsFinite(maxValue) || maxValue <= minValue)
            throw new ArgumentOutOfRangeException(
                nameof(maxValue),
                "Maximum value must be finite and greater than the minimum value."
            );
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(bucketCount),
                "Bucket count must be greater than zero."
            );

        var logSpan = Math.Log(maxValue) - Math.Log(minValue);
        if (!double.IsFinite(logSpan) || logSpan <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxValue),
                "Maximum value must produce a finite logarithmic range greater than zero."
            );

        var logBase = logSpan / bucketCount;
        if (!double.IsFinite(logBase) || logBase <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxValue),
                "Maximum value and bucket count must produce a finite logarithmic bucket span greater than zero."
            );

        _minValue = minValue;
        _maxValue = maxValue;
        _logBase = logBase;
        _base = Math.Exp(_logBase);
        _buckets = new long[bucketCount];
    }

    /// <summary>Record a value.</summary>
    /// <param name="value">Value to record (must be finite and > 0).</param>
    public void Record(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Value must be finite and greater than zero."
            );

        int index;
        if (value <= _minValue)
        {
            index = 0;
        }
        else if (value >= _maxValue)
        {
            index = _buckets.Length - 1;
        }
        else
        {
            var normalizedLog = (Math.Log(value) - Math.Log(_minValue)) / _logBase;
            index = (int)Math.Clamp(
                Math.Floor(normalizedLog),
                0,
                _buckets.Length - 1
            );
        }

        Interlocked.Increment(ref _buckets[index]);
        Interlocked.Increment(ref _totalCount);
    }

    /// <summary>Get approximate p-percentile (0.0-1.0). Returns 0 if no data.</summary>
    /// <param name="p">Percentile to compute (e.g., 0.50 for median).</param>
    /// <returns>Approximate value at the given percentile, or 0 if no data recorded.</returns>
    public double GetPercentile(double p)
    {
        if (!double.IsFinite(p) || p < 0 || p > 1)
            throw new ArgumentOutOfRangeException(
                nameof(p),
                "Percentile must be finite and in the range [0, 1]."
            );

        long total = Interlocked.Read(ref _totalCount);
        if (total == 0)
            return 0.0;

        long target = Math.Max(1L, (long)Math.Ceiling(total * p));
        long cumulative = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            // Use Volatile.Read for non-destructive atomic read — avoids cache line invalidation
            // caused by CompareExchange on other cores. Percentile estimation does not require
            // exact consistency, so a non-destructive read is sufficient.
            cumulative += Volatile.Read(ref _buckets[i]);
            if (cumulative >= target)
                return GetRepresentativeValue(i);
        }
        return GetRepresentativeValue(_buckets.Length - 1);
    }

    /// <summary>Median (p50). Returns 0 if no data.</summary>
    public double P50 => GetPercentile(0.50);

    /// <summary>95th percentile. Returns 0 if no data.</summary>
    public double P95 => GetPercentile(0.95);

    /// <summary>99th percentile. Returns 0 if no data.</summary>
    public double P99 => GetPercentile(0.99);

    private double GetRepresentativeValue(int index)
    {
        var exponent = index + 0.5;
        // Use the bucket's geometric midpoint; fall back to log space to avoid overflow.
        var representative = _minValue * Math.Pow(_base, exponent);
        if (double.IsFinite(representative))
            return representative;

        var representativeLog = Math.Log(_minValue) + (_logBase * exponent);
        if (representativeLog >= Math.Log(double.MaxValue))
            return double.MaxValue;

        return Math.Exp(representativeLog);
    }
}
