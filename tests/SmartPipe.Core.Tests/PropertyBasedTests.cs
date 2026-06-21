using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests;

public class PropertyBasedTests
{
    private static readonly int[] BucketCounts = [1, 2, 7, 64];

    public static TheoryData<ulong> UlongCases => new()
    {
        0UL,
        1UL,
        42UL,
        ulong.MaxValue,
    };

    public static TheoryData<int> PositiveIntCases => new()
    {
        1,
        2,
        7,
        64,
    };

    public static TheoryData<int[]> IntArrayCases
    {
        get
        {
            var data = new TheoryData<int[]>();
            data.Add(Array.Empty<int>());
            data.Add([1]);
            data.Add(Enumerable.Range(1, 25).ToArray());
            data.Add(Enumerable.Range(1, 1_000).ToArray());
            return data;
        }
    }

    public static TheoryData<string> NonNullStringCases => new()
    {
        string.Empty,
        "value",
        "another-value",
    };

    public static TheoryData<int, BackoffStrategy> RetryCountAndStrategyCases => new()
    {
        { 0, BackoffStrategy.Fixed },
        { 1, BackoffStrategy.Exponential },
        { 2, BackoffStrategy.Linear },
        { 16, BackoffStrategy.Exponential },
        { int.MaxValue, BackoffStrategy.Exponential },
    };

    public static TheoryData<string[]> StringArrayCases
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(Array.Empty<string>());
            data.Add(["a"]);
            data.Add(["a", "b"]);
            data.Add(Enumerable.Range(1, 100).Select(static x => $"payload-{x}").ToArray());
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UlongCases))]
    public void DedupFilter_Idempotent(ulong id)
    {
        var filter = new DeduplicationFilter(expectedItems: 1000);

        var first = filter.ContainsAndAdd(id);
        var second = filter.ContainsAndAdd(id);

        first.Should().BeFalse();
        second.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(UlongCases))]
    public void CuckooFilter_ContainsAfterAdd(ulong id)
    {
        var filter = new CuckooFilter(expectedItems: 1000);

        filter.Add(id);

        filter.Contains(id).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(IntArrayCases))]
    public void ReservoirSampler_NeverExceedsCapacity(int[] items)
    {
        var sampler = new ReservoirSampler<int>(capacity: 10);

        foreach (var item in items.Take(1000))
            sampler.Add(item);

        sampler.Sample.Count(x => x != 0).Should().BeLessThanOrEqualTo(10);
    }

    [Theory]
    [MemberData(nameof(UlongCases))]
    public void JumpHash_ValidBucket(ulong key)
    {
        foreach (var numBuckets in BucketCounts)
        {
            var bucket = JumpHash.Hash(key, numBuckets);

            bucket.Should().BeGreaterThanOrEqualTo(0);
            bucket.Should().BeLessThan(numBuckets);
        }
    }

    [Theory]
    [MemberData(nameof(UlongCases))]
    public void JumpHash_Deterministic(ulong key)
    {
        foreach (var numBuckets in BucketCounts)
        {
            JumpHash.Hash(key, numBuckets)
                .Should()
                .Be(JumpHash.Hash(key, numBuckets));
        }
    }

    [Theory]
    [MemberData(nameof(NonNullStringCases))]
    public void ObjectPool_ReturnThenRent(string value)
    {
        var pool = new ObjectPool<string>(() => value, 5);

        var obj = pool.Rent()!;
        pool.Return(obj);
        var obj2 = pool.Rent()!;

        obj2.Should().BeSameAs(obj);
    }

    [Theory]
    [MemberData(nameof(PositiveIntCases))]
    public void RetryPolicy_PositiveDelay(int retryCount)
    {
        var policy = new RetryPolicy(maxRetries: 10);

        policy.GetDelay(retryCount).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Theory]
    [MemberData(nameof(PositiveIntCases))]
    public void RetryPolicy_MonotonicDelay_Exponential(int retryCount)
    {
        var policy = new RetryPolicy(strategy: BackoffStrategy.Exponential);

        var delay = policy.GetDelay(retryCount);
        var nextDelay = policy.GetDelay(retryCount + 1);

        nextDelay.Should().BeGreaterThanOrEqualTo(delay);
    }

    [Theory]
    [MemberData(nameof(PositiveIntCases))]
    public void RetryPolicy_MonotonicDelay_Linear(int retryCount)
    {
        var policy = new RetryPolicy(strategy: BackoffStrategy.Linear);

        var delay = policy.GetDelay(retryCount);
        var nextDelay = policy.GetDelay(retryCount + 1);

        nextDelay.Should().BeGreaterThanOrEqualTo(delay);
    }

    [Theory]
    [MemberData(nameof(RetryCountAndStrategyCases))]
    public void RetryPolicy_BoundedDelay(int retryCount, BackoffStrategy strategy)
    {
        var policy = new RetryPolicy(strategy: strategy);

        policy.GetDelay(retryCount).Should().BeLessThanOrEqualTo(policy.MaxDelay);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void RetryPolicy_OverflowProtection(int retryCount)
    {
        var policy = new RetryPolicy(strategy: BackoffStrategy.Exponential);

        var delay = policy.GetDelay(retryCount);

        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        delay.Should().BeLessThanOrEqualTo(policy.MaxDelay);
    }

    [Theory]
    [MemberData(nameof(StringArrayCases))]
    public void ProcessingEnvelope_UniqueTraceIds(string[] payloads)
    {
        if (payloads.Length < 2)
            return;

        var ids = payloads
            .Take(100)
            .Select(static payload => ProcessingEnvelope<string>.Create(payload).TraceId)
            .ToList();

        ids.Distinct().Should().HaveCount(ids.Count);
    }
}
