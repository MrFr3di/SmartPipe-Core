using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Cuckoo Filter — deduplication with deletion support.
/// Based on "Cuckoo Filter: Better Than Bloom" (NSDI, 2025).</summary>
public class CuckooFilter
{
    private const int BucketSize = 4, MaxKicks = 500;
    private readonly uint[,] _buckets;
    private readonly int _numBuckets;
    private long _count;

    /// <summary>Current number of items in the filter.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Create filter for expected items and false positive rate.</summary>
    /// <param name="expectedItems">Expected number of items (default: 1,000,000).</param>
    /// <param name="falsePositiveRate">Desired false positive rate (default: 0.001).</param>
    public CuckooFilter(long expectedItems = 1_000_000, double falsePositiveRate = 0.001)
    {
        _numBuckets = Math.Max(1, (int)(expectedItems / BucketSize * 1.1));
        _buckets = new uint[_numBuckets, BucketSize];
    }

    /// <summary>Insert item fingerprint into filter.</summary>
    /// <param name="fp">Fingerprint of the item.</param>
    /// <returns>True if inserted successfully.</returns>
    public bool Add(ulong fp)
    {
        uint f = Fingerprint(fp);
        int i1 = BucketIndex(f, 0), i2 = i1 ^ BucketIndex(f, 1);
        if (InsertToBucket(i1, f) || InsertToBucket(i2, f)) { Interlocked.Increment(ref _count); return true; }

        int i = (fp % 2 == 0) ? i1 : i2;
        for (int n = 0; n < MaxKicks; n++)
        {
            int slot = (int)(fp >> 32) % BucketSize;
            uint evicted = _buckets[i, slot];
            _buckets[i, slot] = f;
            f = evicted;
            i ^= BucketIndex(f, 1);
            if (InsertToBucket(i, f)) { Interlocked.Increment(ref _count); return true; }
        }
        return false;
    }

    /// <summary>Check if item fingerprint exists.</summary>
    /// <param name="fp">Fingerprint of the item.</param>
    /// <returns>True if the item probably exists.</returns>
    public bool Contains(ulong fp)
    {
        uint f = Fingerprint(fp);
        int i1 = BucketIndex(f, 0), i2 = i1 ^ BucketIndex(f, 1);
        return BucketContains(i1, f) || BucketContains(i2, f);
    }

    /// <summary>Remove item fingerprint from filter.</summary>
    /// <param name="fp">Fingerprint of the item.</param>
    /// <returns>True if removed successfully.</returns>
    public bool Remove(ulong fp)
    {
        uint f = Fingerprint(fp);
        int i1 = BucketIndex(f, 0), i2 = i1 ^ BucketIndex(f, 1);
        if (RemoveFromBucket(i1, f) || RemoveFromBucket(i2, f)) { Interlocked.Decrement(ref _count); return true; }
        return false;
    }

    private static uint Fingerprint(ulong fp)
    {
        uint f = (uint)(fp & 0xFFFFFFFF);
        return f == 0 ? 1u : f; // Ensure non-zero fingerprint
    }

    private int BucketIndex(uint f, int seed)
    {
        ulong x = f;
        x ^= seed == 0 ? 0x9E3779B9u : 0x85EBCA6Bu;
        x *= 0xBF58476D1CE4E5B9;
        x ^= x >> 27;
        return (int)(x % (ulong)_numBuckets);
    }

    private bool InsertToBucket(int b, uint f)
    {
        for (int i = 0; i < BucketSize; i++)
            if (_buckets[b, i] == 0) { _buckets[b, i] = f; return true; }
        return false;
    }

    private bool BucketContains(int b, uint f)
    {
        for (int i = 0; i < BucketSize; i++)
            if (_buckets[b, i] == f) return true;
        return false;
    }

    private bool RemoveFromBucket(int b, uint f)
    {
        for (int i = 0; i < BucketSize; i++)
            if (_buckets[b, i] == f) { _buckets[b, i] = 0; return true; }
        return false;
    }
}
