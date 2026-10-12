using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

[Trait("Category", "CorrectnessRegression")]
public sealed class CircuitBreakerBucketWindowPrototypeTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    public void BoundaryBurst_DivergenceIsExactlyOldestPartialBucket(int count)
    {
        const long duration = 97;
        var width = (duration + count - 1) / count;
        var time = new ManualTime();
        var exact = new ExactCircuitBreakerSamplingWindow(TimeSpan.FromTicks(duration), time);
        var bucket = new BucketedCircuitBreakerSamplingWindow(TimeSpan.FromTicks(duration), count, time);
        var stream = new List<(long Time, bool Success)>();
        for (var i = 0; i < 4000; i++)
        {
            if (i != 0) time.Advance(i % 7 + 1);
            var success = i % 97 >= 30;
            stream.Add((time.Ticks, success));
            exact.AddSample(success);
            bucket.AddSample(success);
            var expectedExact = stream.Where(sample => time.Ticks - sample.Time <= duration).ToArray();
            var cutoff = System.Math.Max(0, time.Ticks - duration);
            var oldestBucketStart = cutoff / width * width;
            var expectedBuckets = stream.Where(sample => sample.Time >= oldestBucketStart).ToArray();
            var exactStats = exact.ExpireAndGetStatistics();
            var bucketStats = bucket.ExpireAndGetStatistics();
            Assert.Equal(expectedExact.LongLength, exactStats.Total);
            Assert.Equal(expectedExact.LongCount(sample => !sample.Success), exactStats.Failures);
            Assert.Equal(expectedBuckets.LongLength, bucketStats.Total);
            Assert.Equal(expectedBuckets.LongCount(sample => !sample.Success), bucketStats.Failures);
            var boundary = stream.Where(sample => sample.Time >= oldestBucketStart && sample.Time < cutoff).ToArray();
            Assert.Equal(boundary.LongLength, bucketStats.Total - exactStats.Total);
            Assert.Equal(boundary.LongCount(sample => !sample.Success), bucketStats.Failures - exactStats.Failures);
        }
        time.Advance(100_000);
        Assert.Equal(0, bucket.ExpireAndGetStatistics().Total);
        bucket.Reset();
        bucket.AddSample(false);
        Assert.Equal(1, bucket.GetStatistics().Failures);
    }

    [Fact]
    public void TemporalBound_IsNotAFailureRatioBound()
    {
        var time = new ManualTime();
        var exact = new ExactCircuitBreakerSamplingWindow(TimeSpan.FromTicks(97), time);
        var bucket = new BucketedCircuitBreakerSamplingWindow(TimeSpan.FromTicks(97), 7, time);
        for (var i = 0; i < 10_000; i++) { exact.AddSample(false); bucket.AddSample(false); }
        time.Advance(98);
        exact.AddSample(true);
        bucket.AddSample(true);
        Assert.Equal(0, exact.ExpireAndGetStatistics().Failures);
        var stats = bucket.ExpireAndGetStatistics();
        Assert.True((double)stats.Failures / stats.Total > 0.99);
        time.Advance(13); // Bucket [0,14) is now wholly older than the inclusive window.
        Assert.Equal(0, bucket.ExpireAndGetStatistics().Failures);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100_000)]
    public void StorageRemainsBoundedAfterPeakAndIdle(int samples)
    {
        var time = new ManualTime();
        var bucket = new BucketedCircuitBreakerSamplingWindow(TimeSpan.FromSeconds(30), 64, time);
        for (var i = 0; i < samples; i++) bucket.AddSample(i % 5 != 0);
        Assert.Equal(samples, bucket.GetStatistics().Total);
        Assert.Equal(65, bucket.StorageSlotCount);
        time.Advance(TimeSpan.FromMinutes(10).Ticks);
        Assert.Equal(0, bucket.ExpireAndGetStatistics().Total);
        Assert.Equal(65, bucket.StorageSlotCount);
    }

    [Fact]
    public void MaximumWindow_CeilAndRotationDoNotOverflow()
    {
        var time = new ManualTime();
        var bucket = new BucketedCircuitBreakerSamplingWindow(TimeSpan.MaxValue, 2, time);
        bucket.AddSample(false);
        time.Advance(long.MaxValue);
        bucket.AddSample(true);
        Assert.Equal(2, bucket.ExpireAndGetStatistics().Total);
        Assert.Equal(3, bucket.StorageSlotCount);
    }

    [Fact]
    public async Task ConcurrentRecordsAndPhasedReset_HaveExactWholeBucketCounts()
    {
        var time = new ManualTime();
        var bucket = new BucketedCircuitBreakerSamplingWindow(TimeSpan.FromSeconds(30), 64, time);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(async worker =>
        {
            await gate.Task;
            for (var i = 0; i < 1000; i++) bucket.AddSample(worker % 2 == 0);
        }).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(16_000, bucket.GetStatistics().Total);
        Assert.Equal(8_000, bucket.GetStatistics().Failures);
        bucket.Reset();
        Assert.Equal(0, bucket.GetStatistics().Total);
        bucket.AddSample(true);
        Assert.Equal(1, bucket.GetStatistics().Total);
        Assert.Equal(0, bucket.GetStatistics().Failures);
    }

    internal sealed class ManualTime : ICircuitBreakerTimeSource
    {
        public long Ticks;
        public DateTime UtcNow => DateTime.UnixEpoch;
        public long GetTimestamp() => Interlocked.Read(ref Ticks);
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
        public void Advance(long ticks) => Interlocked.Add(ref Ticks, ticks);
    }
}
