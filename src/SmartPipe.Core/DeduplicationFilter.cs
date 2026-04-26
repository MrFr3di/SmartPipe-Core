using System;
using System.Collections;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Bloom filter for deduplication. False positive possible (~0.1%), false negative impossible.
/// Memory: O(1) regardless of items processed.</summary>
public class DeduplicationFilter
{
    private readonly BitArray _bits;
    private readonly int _hashCount, _size;
    private long _itemsSeen;

    /// <summary>Total items seen (incremented on every ContainsAndAdd call).</summary>
    public long ItemsSeen => Interlocked.Read(ref _itemsSeen);

    /// <summary>Create filter for expected items and false positive rate.</summary>
    /// <param name="expectedItems">Expected number of unique items (default: 1,000,000).</param>
    /// <param name="falsePositiveRate">Desired false positive rate (default: 0.001 = 0.1%).</param>
    public DeduplicationFilter(long expectedItems = 1_000_000, double falsePositiveRate = 0.001)
    {
        _size = (int)(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));
        _hashCount = Math.Max(1, (int)((_size / (double)expectedItems) * Math.Log(2)));
        _bits = new BitArray(Math.Max(1024, _size));
    }

    /// <summary>Check if traceId was seen. If not, add it and return false.</summary>
    /// <param name="traceId">Trace ID to check.</param>
    /// <returns>True if the ID was already seen (possible duplicate).</returns>
    public bool ContainsAndAdd(ulong traceId)
    {
        Interlocked.Increment(ref _itemsSeen); // Count every call
        bool allSet = true;
        int h1 = Hash1(traceId), h2 = Hash2(traceId);
        for (int i = 0; i < _hashCount; i++)
        {
            int index = Math.Abs((h1 + i * h2) % _size);
            if (!_bits[index]) { allSet = false; _bits[index] = true; }
        }
        return allSet;
    }

    private static int Hash1(ulong x)
    {
        ulong h = 14695981039346656037;
        for (int i = 0; i < 8; i++) { h ^= (byte)(x >> (i * 8)); h *= 1099511628211; }
        return (int)h;
    }

    private static int Hash2(ulong x)
    {
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCD; x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53; x ^= x >> 33;
        return (int)x;
    }
}
