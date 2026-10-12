using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Resilience;

public sealed class RetryDelayPlannerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void FullJitter_UsesOneSampleWithinCap(long sample)
    {
        var random = new ScriptedRandom(sample);
        var planner = new RetryDelayPlanner(random);
        var policy = new RetryPolicy(delay: TimeSpan.FromTicks(100)) { Jitter = RetryJitterMode.Full };
        Assert.Equal(TimeSpan.FromTicks(sample), planner.GetDelay(policy, 1));
        Assert.Equal(100, random.Bound);
        Assert.Equal(1, random.Calls);
        Assert.Equal(TimeSpan.FromTicks(100), policy.GetDelay(1));
    }

    [Fact]
    public void None_DoesNotDrawAndOverflowUsesMaxDelay()
    {
        var random = new ScriptedRandom(0);
        var planner = new RetryDelayPlanner(random);
        var policy = new RetryPolicy(delay: TimeSpan.FromTicks(100));
        Assert.Equal(policy.MaxDelay, planner.GetDelay(policy, int.MaxValue));
        Assert.Equal(0, random.Calls);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidRefill_IsRejected(double refill) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudgetOptions(1, refill));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLimits_AreRejected(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudgetOptions(limit, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedAttemptAdmissionOptions(limit, TimedAttemptAdmissionMode.Wait));
    }

    [Fact]
    public void Snapshots_CopyOptInOptionsAndJitter()
    {
        var options = new PipelineRuntimeOptions
        {
            RetryBudget = new RetryBudgetOptions(2, 0),
            TimedAttemptAdmission = new TimedAttemptAdmissionOptions(3, TimedAttemptAdmissionMode.Reject),
        };
        var materialized = PipelineRuntimeOptionsSnapshot.Create(options).Materialize();
        Assert.NotSame(options.RetryBudget, materialized.RetryBudget);
        Assert.Equal(2, materialized.RetryBudget!.Capacity);
        Assert.NotSame(options.TimedAttemptAdmission, materialized.TimedAttemptAdmission);
        Assert.Equal(TimedAttemptAdmissionMode.Reject, materialized.TimedAttemptAdmission!.Mode);
        var snapshot = StageFailureOptionsSnapshot.Create(new StageFailureOptions
        {
            Retry = new RetryPolicy { Jitter = RetryJitterMode.Full },
        });
        Assert.Equal(RetryJitterMode.Full, snapshot.Materialize().Retry!.Jitter);
        Assert.Throws<ArgumentOutOfRangeException>(() => StageFailureOptionsSnapshot.Create(new StageFailureOptions
        {
            Retry = new RetryPolicy { Jitter = (RetryJitterMode)99 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedAttemptAdmissionOptions(1, (TimedAttemptAdmissionMode)99));
    }

    private sealed class ScriptedRandom(long sample) : IRetryRandomSource
    {
        public int Calls;
        public long Bound;
        public long NextInt64(long exclusiveUpperBound)
        {
            Calls++;
            Bound = exclusiveUpperBound;
            return sample;
        }
    }
}
