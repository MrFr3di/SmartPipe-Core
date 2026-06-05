#nullable enable

using FluentAssertions;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveParallelismControllerTests
{
    [Fact]
    public void Decide_WhenCooldownHasNotElapsed_ShouldKeepCurrentTargets()
    {
        var controller = new AdaptiveParallelismController(Options());
        var snapshot = Snapshot(
            activeLanes: 2,
            inFlightLimit: 4,
            activeQueuePressure: 1.0,
            timeSinceLastDecision: TimeSpan.FromMilliseconds(100));

        var decision = controller.Decide(snapshot);

        decision.TargetActiveLanes.Should().Be(2);
        decision.TargetInFlightLimit.Should().Be(4);
    }

    [Fact]
    public void Decide_WhenActiveQueuePressureIsSustainedAndFailureRateIsAcceptable_ShouldScaleUpOneStep()
    {
        var controller = new AdaptiveParallelismController(Options());
        var snapshot = Snapshot(
            activeLanes: 2,
            inFlightLimit: 4,
            activeQueuePressure: 0.9,
            processedDelta: 100,
            failedDelta: 1);

        var decision = controller.Decide(snapshot);

        decision.TargetActiveLanes.Should().Be(3);
        decision.TargetInFlightLimit.Should().Be(5);
    }

    [Fact]
    public void Decide_WhenFailureDeltaIsHigh_ShouldScaleDownOneStep()
    {
        var controller = new AdaptiveParallelismController(Options());
        var snapshot = Snapshot(
            activeLanes: 3,
            inFlightLimit: 5,
            activeQueuePressure: 0.8,
            processedDelta: 90,
            failedDelta: 20);

        var decision = controller.Decide(snapshot);

        decision.TargetActiveLanes.Should().Be(2);
        decision.TargetInFlightLimit.Should().Be(4);
    }

    [Fact]
    public void Decide_WhenInactiveBufferedItemsRemainHigh_ShouldNotScaleDownForLowActivePressure()
    {
        var controller = new AdaptiveParallelismController(Options());
        var snapshot = Snapshot(
            activeLanes: 3,
            inFlightLimit: 5,
            activeQueuePressure: 0.05,
            inactiveBufferedItems: 100,
            processedDelta: 100);

        var decision = controller.Decide(snapshot);

        decision.TargetActiveLanes.Should().Be(3);
        decision.TargetInFlightLimit.Should().Be(5);
    }

    [Fact]
    public void Decide_WithIdenticalSnapshots_ShouldBeDeterministic()
    {
        var controller = new AdaptiveParallelismController(Options());
        var snapshot = Snapshot(activeLanes: 2, inFlightLimit: 4, activeQueuePressure: 0.9);

        var first = controller.Decide(snapshot);
        var second = controller.Decide(snapshot);

        first.Should().Be(second);
    }

    private static AdaptiveParallelismOptions Options()
    {
        return new AdaptiveParallelismOptions
        {
            Enabled = true,
            MinDegreeOfParallelism = 1,
            MaxDegreeOfParallelism = 4,
            InitialDegreeOfParallelism = 2,
            InitialInFlightItems = 2,
            MaxInFlightItems = 8,
            SamplingInterval = TimeSpan.FromMilliseconds(100),
            Cooldown = TimeSpan.FromSeconds(1),
            ScaleUpQueuePressure = 0.75,
            ScaleDownQueuePressure = 0.25,
            FailureRateScaleDownThreshold = 0.10,
        };
    }

    private static AdaptiveParallelismSnapshot Snapshot(
        int activeLanes,
        int inFlightLimit,
        double activeQueuePressure,
        long inactiveBufferedItems = 0,
        long processedDelta = 100,
        long failedDelta = 0,
        long retriedDelta = 0,
        TimeSpan? timeSinceLastDecision = null)
    {
        return new AdaptiveParallelismSnapshot(
            Timestamp: DateTimeOffset.UtcNow,
            ActiveLanes: activeLanes,
            TotalLanes: 4,
            ActiveBufferedItems: 10,
            InactiveBufferedItems: inactiveBufferedItems,
            TotalBufferedItems: 10 + inactiveBufferedItems,
            ActiveQueuePressure: activeQueuePressure,
            TotalQueuePressure: activeQueuePressure,
            InFlightItems: System.Math.Min(activeLanes, inFlightLimit),
            InFlightLimit: inFlightLimit,
            ProcessedDelta: processedDelta,
            FailedDelta: failedDelta,
            RetriedDelta: retriedDelta,
            P95Latency: null,
            TimeSinceLastDecision: timeSinceLastDecision ?? TimeSpan.FromSeconds(5));
    }
}
