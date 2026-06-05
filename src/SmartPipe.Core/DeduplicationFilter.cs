#nullable enable

using System;
using System.Collections;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>
/// Bloom-style filter for deduplication. False positives are possible; false negatives are not expected
/// for items that are still inside the retention window.
/// </summary>
/// <remarks>
/// Non-TTL mode behaves like a standard non-deleting Bloom filter. TTL mode uses per-bit counters so
/// expiry of one item does not clear bits still held by another non-expired item. Public methods
/// synchronize access to the internal bitset and TTL counters.
/// </remarks>
public class DeduplicationFilter
{
    private readonly BitArray _bits;
    private readonly int[]? _ttlBitCounts;
    private readonly int _hashCount,
        _size;
    private long _itemsSeen;
    private readonly Lock _bitsLock = new(); // Protects BitArray from concurrent access

    // TTL support: tracks when items were added, cleaned up on ContainsAndAdd
    private readonly TtlEntry[]? _ttlEntries;
    private readonly TimeSpan? _ttl;
    private readonly IClock _clock;
    private long _ttlIndex; // Ring buffer index for TTL entries

    /// <summary>Total items seen (incremented on every ContainsAndAdd call).</summary>
    public long ItemsSeen => Interlocked.Read(ref _itemsSeen);

    /// <summary>Create filter for expected items and false positive rate.</summary>
    /// <param name="expectedItems">Expected number of unique items (default: 1,000,000).</param>
    /// <param name="falsePositiveRate">Desired false positive rate (default: 0.001 = 0.1%).</param>
    /// <param name="ttl">Optional time-to-live. If set, elements are automatically removed after TTL expires.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when expectedItems is not positive or falsePositiveRate is outside (0, 1).</exception>
    public DeduplicationFilter(
        long expectedItems = 1_000_000,
        double falsePositiveRate = 0.001,
        TimeSpan? ttl = null
    )
        : this(expectedItems, falsePositiveRate, ttl, null)
    {
    }

    internal DeduplicationFilter(
        long expectedItems,
        double falsePositiveRate,
        TimeSpan? ttl,
        IClock? clock
    )
    {
        if (expectedItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedItems), expectedItems, "Expected items must be greater than zero.");

        if (double.IsNaN(falsePositiveRate) || falsePositiveRate <= 0 || falsePositiveRate >= 1)
            throw new ArgumentOutOfRangeException(
                nameof(falsePositiveRate),
                falsePositiveRate,
                "False positive rate must be greater than zero and less than one.");

        _size = (int)(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));
        _hashCount = Math.Max(1, (int)(_size / (double)expectedItems * Math.Log(2)));
        _bits = new BitArray(Math.Max(1024, _size));
        _ttl = ttl;
        _clock = clock ?? new TimeProviderClock();

        if (ttl.HasValue)
        {
            _ttlBitCounts = new int[_size];
            // Allocate ring buffer for TTL tracking (1 entry per expected item)
            _ttlEntries = new TtlEntry[Math.Min(expectedItems, 10_000_000)];
        }
    }

    /// <summary>Check if traceId was seen. If not, add it and return false.</summary>
    /// <param name="traceId">Trace ID to check.</param>
    /// <returns>True if the ID was already seen (possible duplicate).</returns>
    public bool ContainsAndAdd(ulong traceId)
    {
        Interlocked.Increment(ref _itemsSeen); // Count every call — atomic, outside lock

        lock (_bitsLock)
        {
            // Cleanup expired TTL entries before checking
            CleanupExpired();

            bool allSet = true;
            int h1 = Hash1(traceId),
                h2 = Hash2(traceId);
            for (int i = 0; i < _hashCount; i++)
            {
                int index = (int)((h1 + (long)i * h2) % _size);
                if (index < 0)
                    index += _size;
                if (!IsSet(index))
                {
                    allSet = false;
                }
            }

            if (!allSet)
                AddBits(h1, h2);

            // Track new entry for TTL
            if (_ttlEntries != null && !allSet)
            {
                long idx = Interlocked.Increment(ref _ttlIndex);
                _ttlEntries[idx % _ttlEntries.Length] = new TtlEntry(traceId, _clock.UtcNow, Occupied: true);
            }

            return allSet;
        }
    }

    /// <summary>Removes expired entries from the filter.</summary>
    private void CleanupExpired()
    {
        if (!_ttl.HasValue || _ttlEntries == null)
            return;

        var cutoff = _clock.UtcNow - _ttl.Value;
        long currentIndex = Interlocked.Read(ref _ttlIndex);

        // Scan only recently added entries (last N entries up to current index)
        long scanFrom = Math.Max(0, currentIndex - _ttlEntries.Length);
        for (long i = scanFrom; i <= currentIndex; i++)
        {
            var entry = _ttlEntries[i % _ttlEntries.Length];
            if (!entry.Occupied)
                continue; // Empty slot
            if (entry.AddedAt < cutoff)
            {
                // Remove expired entry from BitArray
                int h1 = Hash1(entry.TraceId),
                    h2 = Hash2(entry.TraceId);
                for (int j = 0; j < _hashCount; j++)
                {
                    int index = (int)((h1 + (long)j * h2) % _size);
                    if (index < 0)
                        index += _size;
                    RemoveBit(index);
                }
                _ttlEntries[i % _ttlEntries.Length] = default; // Clear slot
            }
        }
    }

    private readonly record struct TtlEntry(ulong TraceId, DateTime AddedAt, bool Occupied);

    private bool IsSet(int index)
    {
        return _ttlBitCounts is null ? _bits[index] : _ttlBitCounts[index] > 0;
    }

    private void AddBits(int h1, int h2)
    {
        for (int i = 0; i < _hashCount; i++)
        {
            int index = (int)((h1 + (long)i * h2) % _size);
            if (index < 0)
                index += _size;

            if (_ttlBitCounts is null)
            {
                _bits[index] = true;
            }
            else
            {
                _ttlBitCounts[index]++;
                _bits[index] = true;
            }
        }
    }

    private void RemoveBit(int index)
    {
        if (_ttlBitCounts is null)
        {
            _bits[index] = false;
            return;
        }

        if (_ttlBitCounts[index] > 0)
            _ttlBitCounts[index]--;

        if (_ttlBitCounts[index] == 0)
            _bits[index] = false;
    }

    private static int Hash1(ulong x)
    {
        ulong h = 14695981039346656037;
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(x >> (i * 8));
            h *= 1099511628211;
        }
        return (int)h;
    }

    private static int Hash2(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCD;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53;
        x ^= x >> 33;
        return (int)x;
    }
}
