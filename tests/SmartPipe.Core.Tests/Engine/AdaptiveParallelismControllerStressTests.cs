#nullable enable

using System.Globalization;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveParallelismControllerStressTests
{
    [Fact]
    public void SustainedLowLatency_GrowsToMaxWithoutExceedingBounds()
    {
        var options = ValidOptions(maxAdjustmentStep: 2);
        var controller = new AdaptiveParallelismController(options);
        var current = options.MinConcurrency;

        for (var step = 0; step < 10; step++)
        {
            var decision = controller.Decide(Snapshot(
                current,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(10)));

            decision.TargetConcurrency.Should().BeInRange(options.MinConcurrency, options.MaxConcurrency);
            current = decision.TargetConcurrency;
        }

        current.Should().Be(options.MaxConcurrency);
    }

    [Fact]
    public void SustainedHighLatency_ShrinksToMinWithoutExceedingBounds()
    {
        var options = ValidOptions(maxAdjustmentStep: 2);
        var controller = new AdaptiveParallelismController(options);
        var current = options.MaxConcurrency;

        for (var step = 0; step < 10; step++)
        {
            var decision = controller.Decide(Snapshot(
                current,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(10)));

            decision.TargetConcurrency.Should().BeInRange(options.MinConcurrency, options.MaxConcurrency);
            current = decision.TargetConcurrency;
        }

        current.Should().Be(options.MinConcurrency);
    }

    [Fact]
    public void TargetAdjacentSamplesInsideDeadZone_KeepLimitStable()
    {
        var options = ValidOptions(deadZone: TimeSpan.FromMilliseconds(10));
        var controller = new AdaptiveParallelismController(options);
        var samples = new[]
        {
            TimeSpan.FromMilliseconds(91),
            TimeSpan.FromMilliseconds(95),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(105),
            TimeSpan.FromMilliseconds(109),
        };

        foreach (var sample in samples)
        {
            var decision = controller.Decide(Snapshot(
                currentLimit: 4,
                latency: sample,
                sinceLastDecision: TimeSpan.FromSeconds(10)));

            decision.TargetConcurrency.Should().Be(4);
            decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.DeadZone);
        }
    }

    [Fact]
    public void SpikeThenRecovery_AdaptsDownThenBackUp()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(minSmoothingFactor: 1));

        var afterSpike = controller.Decide(Snapshot(
            currentLimit: 5,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromSeconds(10)));
        var afterRecovery = controller.Decide(Snapshot(
            currentLimit: afterSpike.TargetConcurrency,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        afterSpike.TargetConcurrency.Should().Be(4);
        afterSpike.Reason.Should().Be(AdaptiveParallelismDecisionReason.HighLatency);
        afterRecovery.TargetConcurrency.Should().Be(5);
        afterRecovery.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Fact]
    public void FailurePressureBlocksGrowthThenRecoversWhenPressureClears()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(minSmoothingFactor: 1));

        var underPressure = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10),
            processedDelta: 10,
            failedDelta: 1));
        var afterRecovery = controller.Decide(Snapshot(
            currentLimit: underPressure.TargetConcurrency,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        underPressure.TargetConcurrency.Should().BeLessThanOrEqualTo(4);
        underPressure.Reason.Should().Be(AdaptiveParallelismDecisionReason.FailurePressure);
        afterRecovery.TargetConcurrency.Should().BeGreaterThan(underPressure.TargetConcurrency);
        afterRecovery.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Fact]
    public void SeededRandomSequence_PreservesControllerInvariants()
    {
        var options = ValidOptions(
            minConcurrency: 1,
            maxConcurrency: 16,
            initialConcurrency: 8,
            maxAdjustmentStep: 3,
            failurePressureThreshold: 0.25);
        var controller = new AdaptiveParallelismController(options);
        var random = new Random(1729);
        var current = options.InitialConcurrency;

        for (var step = 0; step < 1000; step++)
        {
            var sample = TimeSpan.FromMilliseconds(random.Next(-20, 500));
            var sinceLastDecision = random.Next(0, 5) == 0
                ? TimeSpan.FromMilliseconds(50)
                : TimeSpan.FromMilliseconds(500);
            var processed = random.Next(0, 50);
            var failed = random.Next(0, 6) == 0 ? random.Next(0, 3) : 0;
            var snapshotCurrent = step % 149 == 0
                ? int.MaxValue
                : step % 137 == 0
                    ? 0
                    : current;

            var decision = controller.Decide(Snapshot(
                snapshotCurrent,
                sample,
                sinceLastDecision,
                processed,
                failed));
            var because = string.Create(
                CultureInfo.InvariantCulture,
                $"step={step}, sample={sample}, current={snapshotCurrent}, processed={processed}, failed={failed}, since={sinceLastDecision}, previous={decision.PreviousConcurrency}, target={decision.TargetConcurrency}, smoothed={decision.SmoothedLatency}, reason={decision.Reason}");

            decision.PreviousConcurrency.Should().BeInRange(
                options.MinConcurrency,
                options.MaxConcurrency,
                because);
            decision.TargetConcurrency.Should().BeInRange(
                options.MinConcurrency,
                options.MaxConcurrency,
                because);
            System.Math.Abs(decision.TargetConcurrency - decision.PreviousConcurrency)
                .Should().BeLessThanOrEqualTo(options.MaxAdjustmentStep, because);
            decision.SmoothedLatency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero, because);

            if (sinceLastDecision < options.AdjustmentCooldown)
            {
                decision.TargetConcurrency.Should().Be(decision.PreviousConcurrency, because);
                decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.Cooldown, because);
            }

            current = decision.TargetConcurrency;
        }
    }

    private static AdaptiveParallelismOptions ValidOptions(
        int minConcurrency = 1,
        int maxConcurrency = 8,
        int initialConcurrency = 4,
        TimeSpan? targetLatency = null,
        TimeSpan? deadZone = null,
        TimeSpan? cooldown = null,
        int maxAdjustmentStep = 1,
        double failurePressureThreshold = 0.10,
        double minSmoothingFactor = 0.2) =>
        new()
        {
            Enabled = true,
            MinConcurrency = minConcurrency,
            MaxConcurrency = maxConcurrency,
            InitialConcurrency = initialConcurrency,
            TargetLatency = targetLatency ?? TimeSpan.FromMilliseconds(100),
            DeadZone = deadZone ?? TimeSpan.FromMilliseconds(5),
            EvaluationInterval = TimeSpan.FromMilliseconds(100),
            AdjustmentCooldown = cooldown ?? TimeSpan.FromMilliseconds(100),
            MaxAdjustmentStep = maxAdjustmentStep,
            FailurePressureThreshold = failurePressureThreshold,
            MinimumFailureSamples = 10,
            MinSmoothingFactor = minSmoothingFactor,
        };

    private static AdaptiveParallelismSnapshot Snapshot(
        int currentLimit,
        TimeSpan latency,
        TimeSpan sinceLastDecision,
        long processedDelta = 100,
        long failedDelta = 0) =>
        new(
            currentLimit,
            latency,
            processedDelta,
            failedDelta,
            sinceLastDecision);
}
