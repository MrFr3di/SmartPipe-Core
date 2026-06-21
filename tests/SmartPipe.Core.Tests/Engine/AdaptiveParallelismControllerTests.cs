#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveParallelismControllerTests
{
    [Fact]
    public void AdaptiveParallelismOptions_Defaults_DisableAdaptiveMode()
    {
        var options = new PipelineRuntimeOptions();

        options.AdaptiveParallelism.Enabled.Should().BeFalse();
        options.AdaptiveParallelism.MinConcurrency.Should().Be(1);
        options.AdaptiveParallelism.MaxConcurrency.Should().BeGreaterThanOrEqualTo(1);
        options.AdaptiveParallelism.InitialConcurrency.Should().BeInRange(
            options.AdaptiveParallelism.MinConcurrency,
            options.AdaptiveParallelism.MaxConcurrency);
        options.AdaptiveParallelism.TargetLatency.Should().BeGreaterThan(TimeSpan.Zero);
        options.AdaptiveParallelism.DeadZone.Should().BeGreaterThan(TimeSpan.Zero);
        options.AdaptiveParallelism.Cooldown.Should().BeGreaterThan(TimeSpan.Zero);
        options.AdaptiveParallelism.SampleInterval.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, 4, 1)]
    [InlineData(5, 4, 4)]
    [InlineData(2, 4, 1)]
    [InlineData(2, 4, 5)]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveConcurrency(
        int minConcurrency,
        int maxConcurrency,
        int initialConcurrency)
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = ValidOptions(
                minConcurrency: minConcurrency,
                maxConcurrency: maxConcurrency,
                initialConcurrency: initialConcurrency),
        };

        var act = () => options.Validate();

        act.Should().Throw<Exception>()
            .Where(ex => ex.GetType() == typeof(ArgumentOutOfRangeException)
                || ex.GetType() == typeof(InvalidOperationException));
    }

    [Theory]
    [InlineData(0, 5, 100, 1000)]
    [InlineData(100, 0, 100, 1000)]
    [InlineData(100, 5, 0, 1000)]
    [InlineData(100, 5, 100, 0)]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveTiming(
        int targetLatencyMs,
        int deadZoneMs,
        int cooldownMs,
        int sampleIntervalMs)
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = ValidOptions(
                targetLatency: TimeSpan.FromMilliseconds(targetLatencyMs),
                deadZone: TimeSpan.FromMilliseconds(deadZoneMs),
                cooldown: TimeSpan.FromMilliseconds(cooldownMs),
                sampleInterval: TimeSpan.FromMilliseconds(sampleIntervalMs)),
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Decide_HighLatency_ReducesLimitByOneStep()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(200),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(3);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.HighLatency);
    }

    [Fact]
    public void Decide_LowLatency_GrowsLimitByOneStep()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 3,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(4);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Fact]
    public void Decide_WithinDeadZone_KeepsLimitStable()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 3,
            latency: TimeSpan.FromMilliseconds(104),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(3);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.DeadZone);
    }

    [Fact]
    public void Decide_AtMinOrMax_ClampsLimit()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var minDecision = controller.Decide(Snapshot(
            currentLimit: 1,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromSeconds(10)));
        var maxDecision = controller.Decide(Snapshot(
            currentLimit: 8,
            latency: TimeSpan.FromMilliseconds(1),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        minDecision.TargetConcurrency.Should().Be(1);
        minDecision.Reason.Should().Be(AdaptiveParallelismDecisionReason.AtMin);
        maxDecision.TargetConcurrency.Should().Be(8);
        maxDecision.Reason.Should().Be(AdaptiveParallelismDecisionReason.AtMax);
    }

    [Fact]
    public void Decide_DuringCooldown_KeepsLimitStable()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromMilliseconds(50)));

        decision.TargetConcurrency.Should().Be(4);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.Cooldown);
    }

    [Fact]
    public void Decide_AfterLatencySpike_RecoversWhenLatencyReturnsLow()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var afterSpike = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        var afterRecovery = controller.Decide(Snapshot(
            currentLimit: afterSpike.TargetConcurrency,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        afterSpike.TargetConcurrency.Should().Be(3);
        afterRecovery.TargetConcurrency.Should().Be(4);
        afterRecovery.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Decide_FailureOrRetryPressure_DoesNotIncreaseLimit(
        long failedDelta,
        long retriedDelta)
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10),
            processedDelta: 10,
            failedDelta: failedDelta,
            retriedDelta: retriedDelta));

        decision.TargetConcurrency.Should().BeLessThanOrEqualTo(4);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.FailureOrRetryPressure);
    }

    private static AdaptiveParallelismOptions ValidOptions(
        int minConcurrency = 1,
        int maxConcurrency = 8,
        int initialConcurrency = 4,
        TimeSpan? targetLatency = null,
        TimeSpan? deadZone = null,
        TimeSpan? cooldown = null,
        TimeSpan? sampleInterval = null) =>
        new()
        {
            Enabled = true,
            MinConcurrency = minConcurrency,
            MaxConcurrency = maxConcurrency,
            InitialConcurrency = initialConcurrency,
            TargetLatency = targetLatency ?? TimeSpan.FromMilliseconds(100),
            DeadZone = deadZone ?? TimeSpan.FromMilliseconds(5),
            Cooldown = cooldown ?? TimeSpan.FromMilliseconds(100),
            SampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1),
        };

    private static AdaptiveParallelismSnapshot Snapshot(
        int currentLimit,
        TimeSpan latency,
        TimeSpan sinceLastDecision,
        long processedDelta = 100,
        long failedDelta = 0,
        long retriedDelta = 0) =>
        new(
            currentLimit,
            latency,
            processedDelta,
            failedDelta,
            retriedDelta,
            sinceLastDecision);
}
