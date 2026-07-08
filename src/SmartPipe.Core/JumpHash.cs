#nullable enable

using System;

namespace SmartPipe.Core;

/// <summary>Jump Consistent Hash for deterministic sharding with minimal memory.</summary>
/// <remarks>
/// Based on Lamping and Veach, "A Fast, Minimal Memory, Consistent Hash Algorithm".
/// </remarks>
public static class JumpHash
{
    /// <summary>Compute bucket index for a key.</summary>
    /// <param name="key">Key to hash.</param>
    /// <param name="numBuckets">Number of buckets.</param>
    /// <returns>Bucket index in [0, numBuckets).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when numBuckets is not positive.</exception>
    public static int Hash(ulong key, int numBuckets)
    {
        if (numBuckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(numBuckets), numBuckets, "Number of buckets must be greater than zero.");

        unchecked
        {
            long b = -1,
                j = 0;
            while (j < numBuckets)
            {
                b = j;
                key = key * 2862933555777941757 + 1;
                j = (long)((b + 1) * (2147483648.0 / ((key >> 33) + 1)));
            }
            return (int)b;
        }
    }
}
