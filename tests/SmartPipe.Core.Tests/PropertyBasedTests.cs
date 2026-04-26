using FsCheck;
using FsCheck.Xunit;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests;

public class PropertyBasedTests
{
    // DeduplicationFilter: second insert returns true
    [Property]
    public bool DedupFilter_Idempotent(ulong id)
    {
        var filter = new DeduplicationFilter(expectedItems: 1000);
        bool first = filter.ContainsAndAdd(id);
        bool second = filter.ContainsAndAdd(id);
        return !first && second;
    }

    // CuckooFilter: contains after add
    [Property]
    public bool CuckooFilter_ContainsAfterAdd(ulong id)
    {
        var filter = new CuckooFilter(expectedItems: 1000);
        filter.Add(id);
        return filter.Contains(id);
    }

    // ReservoirSampler: never exceeds capacity
    [Property]
    public bool ReservoirSampler_NeverExceedsCapacity(int[] items)
    {
        var sampler = new ReservoirSampler<int>(capacity: 10);
        foreach (var item in items.Take(1000))
            sampler.Add(item);
        return sampler.Sample.Count(x => x != 0) <= 10;
    }

    // JumpHash: returns valid bucket
    [Property]
    public bool JumpHash_ValidBucket(ulong key, PositiveInt numBuckets)
    {
        int bucket = JumpHash.Hash(key, numBuckets.Item);
        return bucket >= 0 && bucket < numBuckets.Item;
    }

    // JumpHash: deterministic
    [Property]
    public bool JumpHash_Deterministic(ulong key, PositiveInt numBuckets)
    {
        return JumpHash.Hash(key, numBuckets.Item) == JumpHash.Hash(key, numBuckets.Item);
    }

    // ObjectPool: return then rent = same object
    [Property]
    public bool ObjectPool_ReturnThenRent(NonNull<string> value)
    {
        var pool = new ObjectPool<string>(() => value.Item, 5);
        var obj = pool.Rent()!;
        pool.Return(obj);
        var obj2 = pool.Rent()!;
        return ReferenceEquals(obj, obj2);
    }

    // RetryPolicy: GetDelay always returns positive TimeSpan
    [Property]
    public bool RetryPolicy_PositiveDelay(PositiveInt retryCount)
    {
        var policy = new RetryPolicy(maxRetries: 10);
        return policy.GetDelay(retryCount.Item) > TimeSpan.Zero;
    }

    // ProcessingContext: TraceIds are unique
    [Property]
    public bool ProcessingContext_UniqueTraceIds(NonNull<string>[] payloads)
    {
        if (payloads.Length < 2) return true;
        var ids = payloads.Take(100).Select(p => new ProcessingContext<string>(p.Item).TraceId).ToList();
        return ids.Distinct().Count() == ids.Count;
    }
}
