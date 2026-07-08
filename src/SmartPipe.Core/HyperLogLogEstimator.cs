#nullable enable

using System;
using System.Numerics;

namespace SmartPipe.Core;

/// <summary>HyperLogLog distinct count estimator. O(1) memory, zero dependencies.</summary>
/// <remarks>Uses MurmurHash-style mixing for uniform distribution across registers.</remarks>
public class HyperLogLogEstimator
{
    private readonly byte[] _regs;
    private readonly int _m;
    private readonly int _precision;
    private readonly double _alpha;

    /// <summary>Creates a new HyperLogLog estimator.</summary>
    /// <param name="precision">Number of bits for bucket index (4-16).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when precision is out of range.</exception>
    public HyperLogLogEstimator(int precision = 12)
    {
        if (precision < 4 || precision > 16)
            throw new ArgumentOutOfRangeException(nameof(precision));
        _precision = precision;
        _m = 1 << precision;
        _regs = new byte[_m];
        _alpha = precision switch
        {
            4 => 0.673,
            5 => 0.697,
            6 => 0.709,
            _ => 0.7213 / (1 + 1.079 / _m),
        };
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining
    )]
    private static ulong Mix64(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return x;
    }

    /// <summary>Adds a hashed value to the estimator.</summary>
    /// <param name="hash">64-bit hash of the item to track.</param>
    public void Add(ulong hash)
    {
        ulong h = Mix64(hash);
        int idx = (int)(h & (ulong)(_m - 1));

        // Extract remaining bits after the precision bits for rank calculation
        ulong remainingBits = h >> _precision;
        // LeadingZeroCount counts zeros in 64-bit value; subtract _precision to account
        // for the upper zero bits introduced by the right shift
        int rank = BitOperations.LeadingZeroCount(remainingBits) - _precision + 1;
        int maxRank = 64 - _precision + 1;
        rank = Math.Min(rank, maxRank);

        if (rank > _regs[idx])
            _regs[idx] = (byte)rank;
    }

    /// <summary>Estimates the number of distinct items added.</summary>
    /// <returns>Estimated distinct count (may have ~1.6% error).</returns>
    public double Estimate()
    {
        double sum = 0;
        int zeros = 0;
        for (int i = 0; i < _m; i++)
        {
            if (_regs[i] == 0)
            {
                zeros++;
                sum += 1.0;
            }
            else
            {
                int rank = _regs[i];
                sum += Math.ScaleB(1.0, -rank);
            }
        }
        double e = _alpha * _m * _m / sum;
        if (e <= 2.5 * _m && zeros > 0)
            e = _m * Math.Log((double)_m / zeros);
        return e;
    }

    /// <summary>Merges multiple estimators into a new one.</summary>
    /// <param name="es">Estimators to merge (must have same precision).</param>
    /// <returns>New estimator with combined data.</returns>
    public static HyperLogLogEstimator Merge(params HyperLogLogEstimator[] es)
    {
        ArgumentNullException.ThrowIfNull(es);
        if (es.Length == 0)
            throw new ArgumentException("At least one estimator is required.", nameof(es));

        var first = es[0] ?? throw new ArgumentNullException(
            nameof(es),
            "Estimators cannot contain null elements."
        );
        int precision = first._precision;

        foreach (var e in es)
        {
            if (e is null)
                throw new ArgumentNullException(
                    nameof(es),
                    "Estimators cannot contain null elements."
                );
            if (e._precision != precision)
                throw new ArgumentException(
                    "All estimators must have the same precision.",
                    nameof(es)
                );
        }

        var m = new HyperLogLogEstimator(precision);
        for (int i = 0; i < m._m; i++)
        {
            foreach (var e in es)
            {
                if (e._regs[i] > m._regs[i])
                    m._regs[i] = e._regs[i];
            }
        }
        return m;
    }
}
