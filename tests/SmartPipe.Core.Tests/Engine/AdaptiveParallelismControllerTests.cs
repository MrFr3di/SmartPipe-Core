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
        options.AdaptiveParallelism.MaxAdjustmentStep.Should().Be(1);
        options.AdaptiveParallelism.FailurePressureThreshold.Should().Be(0.10);
        options.AdaptiveParallelism.MinSmoothingFactor.Should().Be(0.2);
    }

    [Theory]
    [InlineData(0, 4, 1, "MinConcurrency")]
    [InlineData(5, 4, 4, "MaxConcurrency")]
    [InlineData(2, 4, 1, "InitialConcurrency")]
    [InlineData(2, 4, 5, "InitialConcurrency")]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveConcurrency(
        int minConcurrency,
        int maxConcurrency,
        int initialConcurrency,
        string paramName)
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = ValidOptions(
                minConcurrency: minConcurrency,
                maxConcurrency: maxConcurrency,
                initialConcurrency: initialConcurrency),
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(paramName);
    }

    [Theory]
    [InlineData(0, 5, 100, 1000, "TargetLatency")]
    [InlineData(100, 0, 100, 1000, "DeadZone")]
    [InlineData(100, 5, 0, 1000, "Cooldown")]
    [InlineData(100, 5, 100, 0, "SampleInterval")]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveTiming(
        int targetLatencyMs,
        int deadZoneMs,
        int cooldownMs,
        int sampleIntervalMs,
        string paramName)
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

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(paramName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveMaxAdjustmentStep(
        int maxAdjustmentStep)
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = ValidOptions(maxAdjustmentStep: maxAdjustmentStep),
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MaxAdjustmentStep");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.01)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveFailurePressureThreshold(
        double failurePressureThreshold)
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = ValidOptions(failurePressureThreshold: failurePressureThreshold),
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("FailurePressureThreshold");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.01)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PipelineRuntimeOptions_Validate_RejectsInvalidAdaptiveMinSmoothingFactor(
        double minSmoothingFactor)
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = ValidOptions(minSmoothingFactor: minSmoothingFactor),
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MinSmoothingFactor");
    }

    [Fact]
    public void Decide_MinSmoothingFactorOne_UsesLatestSample()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(minSmoothingFactor: 1));

        _ = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(80),
            sinceLastDecision: TimeSpan.FromSeconds(10)));
        var decision = controller.Decide(Snapshot(
            currentLimit: 5,
            latency: TimeSpan.FromMilliseconds(90),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.SmoothedLatency.Should().BeCloseTo(
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromTicks(1));
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
    public void Decide_HighLatency_UsesConfiguredMaxAdjustmentStep()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(maxAdjustmentStep: 2));

        var decision = controller.Decide(Snapshot(
            currentLimit: 6,
            latency: TimeSpan.FromMilliseconds(200),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(4);
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
    public void Decide_LowLatency_UsesConfiguredMaxAdjustmentStep()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(maxAdjustmentStep: 2));

        var decision = controller.Decide(Snapshot(
            currentLimit: 3,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(5);
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
    public void Decide_HighLatencyNearMin_ClampsToMin()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(maxAdjustmentStep: 3));

        var decision = controller.Decide(Snapshot(
            currentLimit: 2,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(1);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.HighLatency);
    }

    [Fact]
    public void Decide_LowLatencyNearMax_ClampsToMax()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(maxAdjustmentStep: 3));

        var decision = controller.Decide(Snapshot(
            currentLimit: 7,
            latency: TimeSpan.FromMilliseconds(1),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(8);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Fact]
    public void Decide_MaxAdjustmentStepIntMax_DoesNotOverflowWhenGrowing()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(
            maxConcurrency: int.MaxValue,
            initialConcurrency: int.MaxValue - 2,
            maxAdjustmentStep: int.MaxValue));

        var decision = controller.Decide(Snapshot(
            currentLimit: int.MaxValue - 2,
            latency: TimeSpan.FromMilliseconds(1),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(int.MaxValue);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Fact]
    public void Decide_MaxAdjustmentStepIntMax_DoesNotOverflowWhenShrinking()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(maxAdjustmentStep: int.MaxValue));

        var decision = controller.Decide(Snapshot(
            currentLimit: 2,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.TargetConcurrency.Should().Be(1);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.HighLatency);
    }

    [Fact]
    public void Decide_CurrentConcurrencyBelowMin_IsClampedBeforeDecision()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 0,
            latency: TimeSpan.FromMilliseconds(500),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.PreviousConcurrency.Should().Be(1);
        decision.TargetConcurrency.Should().Be(1);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.AtMin);
    }

    [Fact]
    public void Decide_CurrentConcurrencyAboveMax_IsClampedBeforeDecision()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 999,
            latency: TimeSpan.FromMilliseconds(1),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.PreviousConcurrency.Should().Be(8);
        decision.TargetConcurrency.Should().Be(8);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.AtMax);
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
    public void Decide_PressureDuringCooldown_KeepsClampedLimitStable()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 999,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromMilliseconds(50),
            processedDelta: 0,
            failedDelta: 1));

        decision.PreviousConcurrency.Should().Be(8);
        decision.TargetConcurrency.Should().Be(8);
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

        decision.TargetConcurrency.Should().Be(3);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.FailureOrRetryPressure);
    }

    [Fact]
    public void Decide_FailureOrRetryPressureAtMin_DoesNotGoBelowMin()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 1,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10),
            processedDelta: 10,
            failedDelta: 1));

        decision.TargetConcurrency.Should().Be(1);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.FailureOrRetryPressure);
    }

    [Fact]
    public void Decide_ZeroProcessedWithFailure_CountsAsPressure()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10),
            processedDelta: 0,
            failedDelta: 1));

        decision.TargetConcurrency.Should().Be(3);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.FailureOrRetryPressure);
    }

    [Fact]
    public void Decide_FailurePressureAtThreshold_CountsAsPressure()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(failurePressureThreshold: 0.25));

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10),
            processedDelta: 4,
            failedDelta: 1));

        decision.TargetConcurrency.Should().Be(3);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.FailureOrRetryPressure);
    }

    [Fact]
    public void Decide_FailurePressureBelowThreshold_DoesNotCountAsPressure()
    {
        var controller = new AdaptiveParallelismController(ValidOptions(failurePressureThreshold: 0.25));

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(20),
            sinceLastDecision: TimeSpan.FromSeconds(10),
            processedDelta: 5,
            failedDelta: 1));

        decision.TargetConcurrency.Should().Be(5);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    [Fact]
    public void Decide_NegativeLatencySample_TreatsSampleAsZero()
    {
        var controller = new AdaptiveParallelismController(ValidOptions());

        var decision = controller.Decide(Snapshot(
            currentLimit: 4,
            latency: TimeSpan.FromMilliseconds(-10),
            sinceLastDecision: TimeSpan.FromSeconds(10)));

        decision.SmoothedLatency.Should().Be(TimeSpan.Zero);
        decision.TargetConcurrency.Should().Be(5);
        decision.Reason.Should().Be(AdaptiveParallelismDecisionReason.LowLatency);
    }

    private static AdaptiveParallelismOptions ValidOptions(
        int minConcurrency = 1,
        int maxConcurrency = 8,
        int initialConcurrency = 4,
        TimeSpan? targetLatency = null,
        TimeSpan? deadZone = null,
        TimeSpan? cooldown = null,
        TimeSpan? sampleInterval = null,
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
            Cooldown = cooldown ?? TimeSpan.FromMilliseconds(100),
            SampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1),
            MaxAdjustmentStep = maxAdjustmentStep,
            FailurePressureThreshold = failurePressureThreshold,
            MinSmoothingFactor = minSmoothingFactor,
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
