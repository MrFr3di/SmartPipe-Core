#nullable enable

using System;

namespace SmartPipe.Core;

/// <summary>Jump Consistent Hash — deterministic sharding with O(1) memory and O(1) computation.
/// Based on "A Fast Minimal Consistent Hash" (arXiv, 2025).</summary>
public static class JumpHash
{
    /// <summary>Compute bucket index for a key.</summary>
    /// <param name="key">Key to hash.</param>
    /// <param name="numBuckets">Number of buckets.</param>
    /// <returns>Bucket index in [0, numBuckets).</returns>
    public static int Hash(ulong key, int numBuckets)
    {
        long b = -1, j = 0;
        while (j < numBuckets)
        {
            b = j;
            key = key * 2862933555777941757 + 1;
            j = (long)((b + 1) * (2147483648.0 / ((key >> 33) + 1)));
        }
        return (int)b;
    }
}
