#nullable enable

using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Cuckoo Filter — deduplication with deletion support.
/// Based on "Cuckoo Filter: Better Than Bloom" (NSDI, 2025).
/// Thread-safe: all public methods synchronize access to the internal bucket array.</summary>
public class CuckooFilter
{
    private const int BucketSize = 4;
    private const int MaxKicks = 500;
    private const double TargetLoadFactor = 0.95;

    private readonly uint[,] _buckets;
    private readonly int _numBuckets;
    private readonly int _bucketMask;
    private readonly int _fingerprintBits;
    private long _count;
    private readonly Lock _syncRoot = new(); // Protects _buckets and _count consistency

    /// <summary>Current number of items in the filter.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Create filter for expected items and false positive rate.</summary>
    /// <param name="expectedItems">Expected number of items (default: 1,000,000).</param>
    /// <param name="falsePositiveRate">Desired false positive rate (default: 0.001).</param>
    public CuckooFilter(long expectedItems = 1_000_000, double falsePositiveRate = 0.001)
    {
        if (expectedItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedItems), expectedItems, "Expected item count must be positive.");
        if (!double.IsFinite(falsePositiveRate) || falsePositiveRate <= 0 || falsePositiveRate >= 1)
            throw new ArgumentOutOfRangeException(nameof(falsePositiveRate), falsePositiveRate, "False positive rate must be finite and between 0 and 1.");

        var rawBits = Math.Ceiling(Math.Log2(2.0 * BucketSize / falsePositiveRate));
        if (!double.IsFinite(rawBits) || rawBits > 32)
            throw new ArgumentOutOfRangeException(nameof(falsePositiveRate), falsePositiveRate, "False positive rate requires more than 32 fingerprint bits.");

        var bits = (int)rawBits;
        _fingerprintBits = Math.Max(1, bits);

        var requiredBuckets = Math.Ceiling(expectedItems / (BucketSize * TargetLoadFactor));
        _numBuckets = RoundUpToPowerOfTwo(checked((long)requiredBuckets));
        _bucketMask = _numBuckets - 1;
        _buckets = new uint[_numBuckets, BucketSize];
    }

    /// <summary>Insert an item into the filter.</summary>
    /// <param name="fp">Input value used to derive the item fingerprint.</param>
    /// <returns>True if inserted successfully.</returns>
    /// <remarks>
    /// Membership is approximate and can false-positive. Adding the same input more than once may
    /// consume more than one bucket slot because duplicate detection is not exact.
    /// </remarks>
    public bool Add(ulong fp)
    {
        var hash = Mix64(fp);
        var primaryBucket = (int)hash & _bucketMask;
        var fingerprint = ExtractFingerprint(hash, _fingerprintBits);

        lock (_syncRoot)
        {
            if (TryInsertFingerprint(primaryBucket, fingerprint, hash))
            {
                Interlocked.Increment(ref _count);
                return true;
            }

            return false;
        }
    }

    /// <summary>Check if an item is probably present.</summary>
    /// <param name="fp">Input value used to derive the item fingerprint.</param>
    /// <returns>True if the item probably exists.</returns>
    /// <remarks>Membership is approximate and can false-positive.</remarks>
    public bool Contains(ulong fp)
    {
        GetBucketPair(fp, out var primaryBucket, out var alternateBucket, out var fingerprint);

        lock (_syncRoot)
        {
            return BucketContains(primaryBucket, fingerprint) || BucketContains(alternateBucket, fingerprint);
        }
    }

    /// <summary>Remove one matching item fingerprint from the filter.</summary>
    /// <param name="fp">Input value used to derive the item fingerprint.</param>
    /// <returns>True if removed successfully.</returns>
    /// <remarks>Remove deletes one fingerprint entry. Duplicate adds may require multiple removes.</remarks>
    public bool Remove(ulong fp)
    {
        GetBucketPair(fp, out var primaryBucket, out var alternateBucket, out var fingerprint);

        lock (_syncRoot)
        {
            if (RemoveFromBucket(primaryBucket, fingerprint) || RemoveFromBucket(alternateBucket, fingerprint))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
            return false;
        }
    }

    private bool InsertToBucket(int b, uint f)
    {
        for (int i = 0; i < BucketSize; i++)
            if (_buckets[b, i] == 0)
            {
                _buckets[b, i] = f;
                return true;
            }
        return false;
    }

    private bool BucketContains(int b, uint f)
    {
        for (int i = 0; i < BucketSize; i++)
            if (_buckets[b, i] == f)
                return true;
        return false;
    }

    private bool RemoveFromBucket(int b, uint f)
    {
        for (int i = 0; i < BucketSize; i++)
            if (_buckets[b, i] == f)
            {
                _buckets[b, i] = 0;
                return true;
            }
        return false;
    }

    /// <summary>Merge another CuckooFilter into this one.</summary>
    /// <param name="other">The other CuckooFilter to merge into this one.</param>
    public void Merge(CuckooFilter other)
    {
        if (other == null)
            throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(this, other))
            return;
        if (_numBuckets != other._numBuckets || _fingerprintBits != other._fingerprintBits)
            throw new ArgumentException("Cannot merge filters with incompatible layouts.", nameof(other));

        var sourceEntries = other.SnapshotEntries();

        lock (_syncRoot)
        {
            var bucketSnapshot = (uint[,])_buckets.Clone();
            var countSnapshot = Interlocked.Read(ref _count);

            foreach (var entry in sourceEntries)
            {
                if (TryInsertFingerprint(entry.SourceBucket, entry.Fingerprint, MergeSeed(entry)))
                {
                    Interlocked.Increment(ref _count);
                    continue;
                }

                Array.Copy(bucketSnapshot, _buckets, bucketSnapshot.Length);
                Interlocked.Exchange(ref _count, countSnapshot);
                throw new InvalidOperationException("Cannot merge filters because the destination cannot fit all source entries.");
            }
        }
    }

    private void GetBucketPair(ulong value, out int primaryBucket, out int alternateBucket, out uint fingerprint)
    {
        var hash = Mix64(value);
        primaryBucket = (int)hash & _bucketMask;
        fingerprint = ExtractFingerprint(hash, _fingerprintBits);
        alternateBucket = AlternateBucket(primaryBucket, fingerprint);
    }

    private int AlternateBucket(int bucket, uint fingerprint)
    {
        if (_numBuckets == 1)
            return 0;

        var delta = (int)MixFingerprint(fingerprint) & _bucketMask;
        if (delta == 0)
            delta = 1;

        return bucket ^ delta;
    }

    private bool TryInsertFingerprint(int primaryBucket, uint fingerprint, ulong seed)
    {
        var alternateBucket = AlternateBucket(primaryBucket, fingerprint);
        if (InsertToBucket(primaryBucket, fingerprint) || InsertToBucket(alternateBucket, fingerprint))
            return true;

        Span<BucketMutation> journal = stackalloc BucketMutation[MaxKicks];
        var journalCount = 0;
        var currentFingerprint = fingerprint;
        var currentBucket = (seed & 1) == 0 ? primaryBucket : alternateBucket;

        for (var kick = 0; kick < MaxKicks; kick++)
        {
            var slot = (int)(Mix64(seed + (ulong)kick) % BucketSize);
            var evicted = _buckets[currentBucket, slot];
            journal[journalCount++] = new BucketMutation(currentBucket, slot, evicted);
            _buckets[currentBucket, slot] = currentFingerprint;

            currentFingerprint = evicted;
            currentBucket = AlternateBucket(currentBucket, currentFingerprint);

            if (InsertToBucket(currentBucket, currentFingerprint))
                return true;
        }

        RestoreMutations(journal[..journalCount]);
        return false;
    }

    private void RestoreMutations(ReadOnlySpan<BucketMutation> mutations)
    {
        for (var i = mutations.Length - 1; i >= 0; i--)
            _buckets[mutations[i].Bucket, mutations[i].Slot] = mutations[i].PreviousFingerprint;
    }

    private BucketEntry[] SnapshotEntries()
    {
        lock (_syncRoot)
        {
            var entryCount = 0;
            for (var bucket = 0; bucket < _numBuckets; bucket++)
                for (var slot = 0; slot < BucketSize; slot++)
                    if (_buckets[bucket, slot] != 0)
                        entryCount++;

            var entries = new BucketEntry[entryCount];
            var index = 0;
            for (var bucket = 0; bucket < _numBuckets; bucket++)
                for (var slot = 0; slot < BucketSize; slot++)
                {
                    var fingerprint = _buckets[bucket, slot];
                    if (fingerprint != 0)
                        entries[index++] = new BucketEntry(bucket, fingerprint);
                }

            return entries;
        }
    }

    private static uint ExtractFingerprint(ulong hash, int bits)
    {
        var fingerprint = (uint)(hash >> (64 - bits));
        return fingerprint == 0 ? 1u : fingerprint;
    }

    private static ulong MixFingerprint(uint fingerprint) => Mix64(fingerprint);

    private static ulong MergeSeed(BucketEntry entry) => MixFingerprint(entry.Fingerprint) ^ (uint)entry.SourceBucket;

    private static ulong Mix64(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    private static int RoundUpToPowerOfTwo(long value)
    {
        if (value <= 1)
            return 1;
        if (value > 1 << 30)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Required bucket count is too large.");

        var result = 1;
        while (result < value)
            result <<= 1;
        return result;
    }

    private readonly struct BucketMutation(int bucket, int slot, uint previousFingerprint)
    {
        public int Bucket { get; } = bucket;

        public int Slot { get; } = slot;

        public uint PreviousFingerprint { get; } = previousFingerprint;
    }

    private readonly struct BucketEntry(int sourceBucket, uint fingerprint)
    {
        public int SourceBucket { get; } = sourceBucket;

        public uint Fingerprint { get; } = fingerprint;
    }
}
