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
        _alpha = precision switch { 4 => 0.673, 5 => 0.697, 6 => 0.709, _ => 0.7213 / (1 + 1.079 / _m) };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong Mix64(ulong x)
    {
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL; x ^= x >> 33;
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
        byte rank = (byte)(BitOperations.LeadingZeroCount(remainingBits) - _precision + 1);
        
        if (rank > _regs[idx]) _regs[idx] = rank;
    }

    /// <summary>Estimates the number of distinct items added.</summary>
    /// <returns>Estimated distinct count (may have ~1.6% error).</returns>
    public double Estimate()
    {
        double sum = 0; int zeros = 0;
        for (int i = 0; i < _m; i++)
        {
            if (_regs[i] == 0) { zeros++; sum += 1.0; }
            else sum += 1.0 / (1 << _regs[i]);
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
        var m = new HyperLogLogEstimator((int)Math.Log2(es[0]._m));
        for (int i = 0; i < m._m; i++)
            foreach (var e in es) if (e._regs[i] > m._regs[i]) m._regs[i] = e._regs[i];
        return m;
    }
}
