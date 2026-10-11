using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

[Trait("Category", "CorrectnessRegression")]
public sealed class CircuitBreakerBucketSamplingTests
{
    [Fact]
    public void DefaultsAndSnapshots_PreserveExplicitModeAndBucketCount()
    {
        Assert.Equal(CircuitBreakerSamplingMode.Exact, new CircuitBreakerOptions().SamplingMode);
        Assert.Equal(64, new CircuitBreakerOptions().SamplingBucketCount);
        Assert.Equal(CircuitBreakerSamplingMode.Exact, new CircuitBreakerPolicy().SamplingMode);
        var policy = new CircuitBreakerPolicy { SamplingMode = CircuitBreakerSamplingMode.Bucketed, SamplingBucketCount = 7 };
        var materialized = StageFailureOptionsSnapshot.Create(new StageFailureOptions { CircuitBreaker = policy }).Materialize();
        Assert.NotSame(policy, materialized.CircuitBreaker);
        Assert.Equal(CircuitBreakerSamplingMode.Bucketed, materialized.CircuitBreaker!.SamplingMode);
        Assert.Equal(7, materialized.CircuitBreaker.SamplingBucketCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4097)]
    public void InvalidBucketCount_IsRejectedInOptionsAndStageSnapshots(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(new CircuitBreakerOptions { SamplingBucketCount = count }, TimeProvider.System));
        Assert.Throws<ArgumentOutOfRangeException>(() => StageFailureOptionsSnapshot.Create(new StageFailureOptions
        {
            CircuitBreaker = new CircuitBreakerPolicy { SamplingBucketCount = count },
        }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidMode_IsRejectedBeforeFactories(int mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(new CircuitBreakerOptions { SamplingMode = (CircuitBreakerSamplingMode)mode }, TimeProvider.System));
        var activations = 0;
        var builder = PipelineDefinitionBuilder.From(new PipelineKey("invalid-bucket"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            {
                activations++;
                return ValueTask.FromResult<IPipelineSource<int>>(new SmartPipe.Core.Tests.Engine.RetryAdmissionRegressionTests.Source(1));
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Transform(new PipelineStageKey("invalid"),
            PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
            {
                activations++;
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(new SmartPipe.Core.Tests.Engine.RetryAdmissionRegressionTests.FailingTransformer());
            }), new StageFailureOptions { CircuitBreaker = new CircuitBreakerPolicy { SamplingMode = (CircuitBreakerSamplingMode)mode } }));
        Assert.Equal(0, activations);
    }

    [Fact]
    public void PublicRatio_ReplaysExactAndExplainedBucketBoundaryStream()
    {
        var time = new SamplingTime();
        var exact = Create(CircuitBreakerSamplingMode.Exact, time);
        var bucket = Create(CircuitBreakerSamplingMode.Bucketed, time);
        var stream = new List<(long Timestamp, bool Success)>();
        for (var i = 0; i < 2000; i++)
        {
            if (i != 0) time.Advance(i % 7 + 1);
            var success = i % 97 >= 30;
            using var exactPermit = exact.AcquirePermit();
            using var bucketPermit = bucket.AcquirePermit();
            Assert.True(exactPermit.IsAllowed);
            Assert.True(bucketPermit.IsAllowed);
            if (success) { exactPermit.RecordSuccess(); bucketPermit.RecordSuccess(); }
            else { exactPermit.RecordFailure(); bucketPermit.RecordFailure(); }
            stream.Add((time.Ticks, success));
            var included = stream.Where(sample => time.Ticks - sample.Timestamp <= 97).ToArray();
            var oldestStart = System.Math.Max(0, time.Ticks - 97) / 14 * 14;
            var approximate = stream.Where(sample => sample.Timestamp >= oldestStart).ToArray();
            Assert.Equal(included.Count(sample => !sample.Success) / (double)included.Length, exact.GetCurrentFailureRatio(), precision: 12);
            Assert.Equal(approximate.Count(sample => !sample.Success) / (double)approximate.Length, bucket.GetCurrentFailureRatio(), precision: 12);
        }
        time.Advance(100_000);
        Assert.Equal(0, exact.GetCurrentFailureRatio());
        Assert.Equal(0, bucket.GetCurrentFailureRatio());
        bucket.Reset();
        Assert.Equal(CircuitState.Closed, bucket.State);
        Assert.Equal(0, bucket.GetCurrentFailureRatio());
    }

    [Theory]
    [InlineData(CircuitBreakerSamplingMode.Exact)]
    [InlineData(CircuitBreakerSamplingMode.Bucketed)]
    public void HalfOpenGenerationsAndReleasedProbeSlots_AreIndependentOfSamplingMode(CircuitBreakerSamplingMode mode)
    {
        var time = new SamplingTime();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            SamplingMode = mode,
            SamplingBucketCount = 7,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromTicks(97),
            MinimumThroughput = 2,
            BreakDuration = TimeSpan.FromTicks(10),
            MaxHalfOpenRequests = 2,
        }, time);
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Equal(CircuitState.Open, breaker.State);
        time.Advance(10);
        using var old = breaker.AcquirePermit();
        using var sibling = breaker.AcquirePermit();
        using var rejected = breaker.AcquirePermit();
        Assert.True(old.IsAllowed);
        Assert.True(sibling.IsAllowed);
        Assert.False(rejected.IsAllowed);
        old.RecordFailure();
        sibling.RecordSuccess();
        Assert.Equal(CircuitState.Open, breaker.State);
        time.Advance(10);
        var unrecorded = breaker.AcquirePermit();
        Assert.True(unrecorded.IsAllowed);
        unrecorded.Dispose();
        using var first = breaker.AcquirePermit();
        using var second = breaker.AcquirePermit();
        Assert.True(first.IsAllowed);
        Assert.True(second.IsAllowed);
        first.RecordSuccess();
        second.RecordSuccess();
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void BoundaryBurst_CanChangeOpeningOnlyWhileOldestPartialBucketIsRetained()
    {
        var time = new SamplingTime();
        CircuitBreaker Make(CircuitBreakerSamplingMode mode) => new(new CircuitBreakerOptions
        {
            SamplingMode = mode,
            SamplingBucketCount = 2,
            SamplingDuration = TimeSpan.FromTicks(100),
            FailureRatio = 0.4,
            MinimumThroughput = 100,
        }, time);
        var exact = Make(CircuitBreakerSamplingMode.Exact);
        var bucket = Make(CircuitBreakerSamplingMode.Bucketed);
        for (var i = 0; i < 49; i++) { exact.RecordFailure(); bucket.RecordFailure(); }
        Assert.Equal(CircuitState.Closed, exact.State);
        Assert.Equal(CircuitState.Closed, bucket.State);
        time.Advance(100);
        for (var i = 0; i < 100; i++) { exact.RecordSuccess(); bucket.RecordSuccess(); }
        time.Advance(1);
        exact.RecordFailure();
        bucket.RecordFailure();
        Assert.Equal(CircuitState.Closed, exact.State);
        Assert.Equal(CircuitState.Open, bucket.State);
        Assert.Equal(1d / 101, exact.GetCurrentFailureRatio(), precision: 12);
        Assert.True(bucket.GetCurrentFailureRatio() >= 0.4);
        time.Advance(49);
        Assert.Equal(exact.GetCurrentFailureRatio(), bucket.GetCurrentFailureRatio(), precision: 12);
        // State does not automatically close when history expires; the usual probe protocol still applies.
        Assert.Equal(CircuitState.Open, bucket.State);
    }

    [Theory]
    [InlineData(CircuitBreakerSamplingMode.Exact)]
    [InlineData(CircuitBreakerSamplingMode.Bucketed)]
    public void EwmaSyntheticSamplesAndReset_PreserveHybridDetection(CircuitBreakerSamplingMode mode)
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            SamplingMode = mode,
            FailureRatio = 0.5,
            MinimumThroughput = int.MaxValue,
        }, new SamplingTime());
        for (var i = 0; i < 5; i++) breaker.RecordFailure();
        breaker.RecordSuccess();
        Assert.Equal(8d / 9, breaker.GetCurrentFailureRatio(), precision: 12);
        Assert.Equal(0.475, (double)breaker.GetMetrics()["cb_ewma_failure_rate"], precision: 12);
        breaker.Reset();
        Assert.Equal(0, breaker.GetCurrentFailureRatio());
        Assert.Equal(0d, breaker.GetMetrics()["cb_ewma_failure_rate"]);
    }

    private static CircuitBreaker Create(CircuitBreakerSamplingMode mode, SamplingTime time) => new(new CircuitBreakerOptions
    {
        SamplingMode = mode,
        SamplingBucketCount = 7,
        SamplingDuration = TimeSpan.FromTicks(97),
        FailureRatio = 1,
        MinimumThroughput = int.MaxValue,
    }, time);

    private sealed class SamplingTime : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Ticks);
        public void Advance(long ticks) => Ticks += ticks;
    }
}
