#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveParallelismRuntimeStateTests
{
    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var act = () => new AdaptiveParallelismRuntimeState(null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("options");
    }

    [Fact]
    public void Constructor_InvalidOptions_ThrowsFromValidation()
    {
        var options = new PipelineRuntimeOptions
        {
            AdaptiveParallelism = new AdaptiveParallelismOptions
            {
                Enabled = true,
                MinConcurrency = 0,
            },
        };

        var act = () => new AdaptiveParallelismRuntimeState(options);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MinConcurrency");
    }

    [Fact]
    public void CurrentLimit_ReflectsInitialConcurrency()
    {
        var options = ValidOptions(initialConcurrency: 3, maxConcurrency: 4, runtimeMaxConcurrency: 8);

        using var state = new AdaptiveParallelismRuntimeState(options);

        state.CurrentLimit.Should().Be(3);
    }

    [Fact]
    public void CurrentLimit_ClampedToEffectiveMaxConcurrency()
    {
        // When adaptive max > runtime max, effective adaptive max = runtime max.
        var options = ValidOptions(initialConcurrency: 8, maxConcurrency: 8, runtimeMaxConcurrency: 4);

        using var state = new AdaptiveParallelismRuntimeState(options);

        state.CurrentLimit.Should().Be(4);
    }

    [Fact]
    public async Task AcquireAsync_WithAvailableCapacity_ReturnsLease()
    {
        var options = ValidOptions(initialConcurrency: 2, maxConcurrency: 4);

        using var state = new AdaptiveParallelismRuntimeState(options);

        var first = await state.AcquireAsync(CancellationToken.None);
        var second = await state.AcquireAsync(CancellationToken.None);
        var queued = state.AcquireAsync(CancellationToken.None);

        queued.IsCompleted.Should().BeFalse();

        first.Dispose();
        var third = await queued;
        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_AfterComplete_ThrowsObjectDisposedException()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 2);
        var state = new AdaptiveParallelismRuntimeState(options);

        state.Complete();

        await FluentActions.Awaiting(async () => await state.AcquireAsync(CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task AcquireAsync_WithPreCancelledToken_ReturnsCancelledTask()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 1);
        using var state = new AdaptiveParallelismRuntimeState(options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = state.AcquireAsync(cts.Token).AsTask();

        task.IsCanceled.Should().BeTrue();
    }

    [Fact]
    public void RecordCompletion_LowLatency_IncreasesLimit()
    {
        // Cooldown of 1 tick ensures we get a decision on every completion.
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 1,
            maxConcurrency: 4,
            cooldownTicks: 1,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: false);

        state.CurrentLimit.Should().BeGreaterThan(1);
    }

    [Fact]
    public void RecordCompletion_HighLatency_ReducesLimit()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 4,
            maxConcurrency: 4,
            cooldownTicks: 1,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        state.RecordCompletion(TimeSpan.FromMilliseconds(500), failed: false);

        state.CurrentLimit.Should().BeLessThan(4);
    }

    [Fact]
    public void RecordCompletion_UsesIntervalAverageLatencyForControllerDecision()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 2,
            maxConcurrency: 4,
            evaluationIntervalTicks: TimeSpan.FromMilliseconds(10).Ticks,
            adjustmentCooldownTicks: 1,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        clock.Advance(TimeSpan.FromMilliseconds(5));
        state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: false);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        state.RecordCompletion(TimeSpan.FromMilliseconds(150), failed: false);

        state.CurrentLimit.Should().Be(3,
            "the interval average is below target latency, while the last completion alone is above target");
    }

    [Fact]
    public void RecordCompletion_AfterComplete_IsNoOp()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 1,
            maxConcurrency: 4,
            cooldownTicks: 1,
            clock: clock);

        var state = new AdaptiveParallelismRuntimeState(options);
        state.Complete();

        clock.Advance(TimeSpan.FromMilliseconds(2));
        var act = () => state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: false);

        act.Should().NotThrow();
        state.CurrentLimit.Should().Be(1);
    }

    [Fact]
    public void RecordCompletion_FailurePressure_ReducesLimit()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 4,
            maxConcurrency: 4,
            cooldownTicks: 1,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        // One failure out of one processed exceeds the 10% failure pressure threshold.
        state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: true);

        state.CurrentLimit.Should().BeLessThan(4);
    }

    [Fact]
    public void RecordCompletion_BeforeEvaluationInterval_DoesNotChangeLimit()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 4,
            maxConcurrency: 4,
            evaluationIntervalTicks: TimeSpan.FromMilliseconds(10).Ticks,
            adjustmentCooldownTicks: 1,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        clock.Advance(TimeSpan.FromMilliseconds(5));
        state.RecordCompletion(TimeSpan.FromMilliseconds(500), failed: false);

        state.CurrentLimit.Should().Be(4);
    }

    [Fact]
    public void RecordCompletion_FailurePressureBelowMinimumSamples_DoesNotReduceLimit()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 4,
            maxConcurrency: 4,
            evaluationIntervalTicks: 1,
            adjustmentCooldownTicks: 1,
            minimumFailureSamples: 10,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        for (var i = 0; i < 9; i++)
        {
            state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: true);
            clock.Advance(TimeSpan.FromMilliseconds(2));
        }

        state.CurrentLimit.Should().Be(4);
    }

    [Fact]
    public void RecordCompletion_WhenCooldownBlocksDecision_ResetsIntervalCounters()
    {
        var clock = new ManualPipelineClock();
        var options = ValidOptions(
            initialConcurrency: 4,
            maxConcurrency: 4,
            evaluationIntervalTicks: 1,
            adjustmentCooldownTicks: TimeSpan.FromSeconds(10).Ticks,
            minimumFailureSamples: 10,
            clock: clock);

        using var state = new AdaptiveParallelismRuntimeState(options);

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(2));
            state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: true);
        }

        state.CurrentLimit.Should().Be(4, "adjustment cooldown blocks the first interval decision");

        clock.Advance(TimeSpan.FromSeconds(11));
        state.RecordCompletion(TimeSpan.FromMilliseconds(1), failed: false);

        state.CurrentLimit.Should().Be(4,
            "the previous failed interval must not carry into the next evaluation");
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 2);
        var state = new AdaptiveParallelismRuntimeState(options);

        state.Complete();
        var act = () => state.Complete();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_CallsComplete()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 2);
        var state = new AdaptiveParallelismRuntimeState(options);

        state.Dispose();

        // After dispose, acquiring should throw ObjectDisposedException (limiter completed).
        await FluentActions.Awaiting(async () => await state.AcquireAsync(CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_CallsComplete()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 2);
        var state = new AdaptiveParallelismRuntimeState(options);

        await state.DisposeAsync();

        await FluentActions.Awaiting(async () => await state.AcquireAsync(CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 2);
        var state = new AdaptiveParallelismRuntimeState(options);

        state.Dispose();
        var act = () => state.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AcquireAsync_WhenQueuedAndComplete_UnblocksWithException()
    {
        var options = ValidOptions(initialConcurrency: 1, maxConcurrency: 1);
        var state = new AdaptiveParallelismRuntimeState(options);

        var lease = await state.AcquireAsync(CancellationToken.None);
        try
        {
            var queued = state.AcquireAsync(CancellationToken.None).AsTask();

            state.Complete();

            await FluentActions.Awaiting(() => queued)
                .Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static PipelineRuntimeOptions ValidOptions(
        int initialConcurrency = 1,
        int maxConcurrency = 4,
        int runtimeMaxConcurrency = 8,
        long cooldownTicks = 1,
        long? evaluationIntervalTicks = null,
        long? adjustmentCooldownTicks = null,
        int minimumFailureSamples = 1,
        IPipelineClock? clock = null) =>
        new()
        {
            MaxConcurrency = runtimeMaxConcurrency,
            Clock = clock ?? SystemPipelineClock.Instance,
            AdaptiveParallelism = new AdaptiveParallelismOptions
            {
                Enabled = true,
                MinConcurrency = 1,
                MaxConcurrency = maxConcurrency,
                InitialConcurrency = initialConcurrency,
                TargetLatency = TimeSpan.FromMilliseconds(100),
                DeadZone = TimeSpan.FromMilliseconds(5),
                EvaluationInterval = TimeSpan.FromTicks(evaluationIntervalTicks ?? cooldownTicks),
                AdjustmentCooldown = TimeSpan.FromTicks(adjustmentCooldownTicks ?? cooldownTicks),
                MaxAdjustmentStep = 1,
                FailurePressureThreshold = 0.10,
                MinimumFailureSamples = minimumFailureSamples,
                MinSmoothingFactor = 1.0,
            },
        };

    private sealed class ManualPipelineClock : IPipelineClock
    {
        private readonly object _gate = new();
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        public DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return new DateTimeOffset(_ticks, TimeSpan.Zero);
        }

        public long GetTimestamp()
        {
            lock (_gate)
                return _ticks;
        }

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan value)
        {
            lock (_gate)
                _ticks += value.Ticks;
        }
    }
}
