#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class TypedPipelineAdaptiveParallelismTests
{
    [Fact]
    public async Task AdaptiveDisabled_PreservesParallelProcessing()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 8));
        var transformer = new TrackingTransformer(clock, _ => TimeSpan.Zero, _ => true);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                InputCapacity = 4,
                Clock = clock,
            })
            .Run();

        await transformer.WaitForActiveAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().BeGreaterThan(1);

        transformer.Release();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AdaptiveEnabled_NeverExceedsInitialLimitBeforeGrowth()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 8), emittedThreshold: 4);
        var transformer = new TrackingTransformer(clock, _ => TimeSpan.Zero, _ => true);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(clock, maxConcurrency: 4, initialConcurrency: 1))
            .Run();

        await transformer.WaitForActiveAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await source.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        transformer.MaxObservedConcurrency.Should().Be(1);

        transformer.Release();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AdaptiveEnabled_NeverExceedsEffectiveMaxConcurrency()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 16));
        var transformer = new TrackingTransformer(
            clock,
            _ => TimeSpan.FromMilliseconds(1),
            payload => payload >= 6);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(
                clock,
                maxConcurrency: 2,
                initialConcurrency: 1,
                adaptiveMaxConcurrency: 8))
            .Run();

        await transformer.WaitForActiveAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(2);

        transformer.Release();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task AdaptiveEnabled_HighLatency_ReducesObservedConcurrency()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 12));
        var transformer = new TrackingTransformer(
            clock,
            payload => payload <= 3 ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero,
            payload => payload >= 4);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(
                clock,
                maxConcurrency: 4,
                initialConcurrency: 3,
                minConcurrency: 1,
                adaptiveMaxConcurrency: 4))
            .Run();

        await transformer.WaitForCompletedAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.WaitForActiveAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

        transformer.ActiveCalls.Should().BeLessThanOrEqualTo(2);

        transformer.Release();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AdaptiveEnabled_LowLatency_RecoversObservedConcurrency()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 16));
        var transformer = new TrackingTransformer(
            clock,
            _ => TimeSpan.FromMilliseconds(1),
            payload => payload >= 5);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(
                clock,
                maxConcurrency: 4,
                initialConcurrency: 1,
                adaptiveMaxConcurrency: 3))
            .Run();

        await transformer.WaitForActiveAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().Be(3);

        transformer.Release();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task AdaptiveEnabled_DrainCompletesAcceptedWork()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 4));
        var transformer = new TrackingTransformer(clock, _ => TimeSpan.Zero, _ => true);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(clock, maxConcurrency: 4, initialConcurrency: 2))
            .Run();

        await transformer.WaitForActiveAsync(2).WaitAsync(TimeSpan.FromSeconds(5));

        var drain = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        drain.IsCompleted.Should().BeFalse();

        transformer.Release();

        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.CompletedCalls.Should().BeGreaterThan(0);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task AdaptiveEnabled_CancelUnblocksAdmissionWaiters()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 8), emittedThreshold: 4);
        var transformer = new TrackingTransformer(clock, _ => TimeSpan.Zero, _ => true);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(clock, maxConcurrency: 4, initialConcurrency: 1))
            .Run();

        await transformer.WaitForActiveAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await source.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().Be(1);

        await run.CancelAsync();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().NotBe(PipelineRunState.Faulted);
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task AdaptiveEnabled_AbortUnblocksAdmissionWaiters()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 8), emittedThreshold: 4);
        var transformer = new TrackingTransformer(clock, _ => TimeSpan.Zero, _ => true);

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(clock, maxConcurrency: 4, initialConcurrency: 1))
            .Run();

        await transformer.WaitForActiveAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await source.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().Be(1);

        await run.AbortAsync();

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().NotBe(PipelineRunState.Faulted);
        run.State.Should().Be(PipelineRunState.Aborted);
    }

    [Fact]
    public async Task AdaptiveEnabled_StageFailureReleasesPermit()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(1, 2);
        var transformer = new TrackingTransformer(
            clock,
            _ => TimeSpan.FromMilliseconds(1),
            _ => false,
            payload => payload == 1);

        var run = PipelineBuilder
            .From(source)
            .Transform(
                transformer,
                new StageFailureOptions { OnPermanentFailure = FailureAction.Skip })
            .WithRuntimeOptions(Options(clock, maxConcurrency: 2, initialConcurrency: 1))
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        transformer.StartedCalls.Should().Be(2);
        transformer.CompletedCalls.Should().Be(1);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task AdaptiveEnabled_AdmissionWaitTimeIsNotCountedAsProcessingLatency()
    {
        var clock = new ManualPipelineClock();
        var source = new CountingEnvelopeSource(Enumerable.Range(1, 8), emittedThreshold: 2);
        var transformer = new PayloadGateTrackingTransformer(
            clock,
            _ => TimeSpan.FromMilliseconds(1),
            payload => payload switch
            {
                1 => PayloadGate.First,
                >= 4 => PayloadGate.Second,
                _ => PayloadGate.None,
            });

        var run = PipelineBuilder
            .From(source)
            .Transform(transformer)
            .WithRuntimeOptions(Options(
                clock,
                maxConcurrency: 2,
                initialConcurrency: 1,
                minConcurrency: 1,
                adaptiveMaxConcurrency: 2))
            .Run();

        await transformer.WaitForActiveAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await source.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(10));

        transformer.ReleaseFirstGate();

        await transformer.WaitForActiveAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        transformer.MaxObservedConcurrency.Should().Be(2);

        transformer.ReleaseSecondGate();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    public void PipelineRuntimeOptions_Validate_RejectsAdaptiveWithDroppingInputFullMode(
        BoundedChannelFullMode fullMode)
    {
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 4,
            InputCapacity = 4,
            Clock = new ManualPipelineClock(),
            InputFullMode = fullMode,
            AdaptiveParallelism = new AdaptiveParallelismOptions
            {
                Enabled = true,
                MinConcurrency = 1,
                MaxConcurrency = 4,
                InitialConcurrency = 1,
            },
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Adaptive parallelism requires InputFullMode = Wait.");
    }

    [Fact]
    public void PipelineRuntimeOptions_Validate_RejectsAdaptiveMinGreaterThanEffectiveMax()
    {
        var options = Options(
            new ManualPipelineClock(),
            maxConcurrency: 2,
            initialConcurrency: 3,
            minConcurrency: 3,
            adaptiveMaxConcurrency: 8);

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Minimum adaptive concurrency*effective adaptive maximum*");
    }

    [Fact]
    public void PipelineRuntimeOptions_Validate_DoesNotApplyAdaptiveInputFullModeConstraintWhenDisabled()
    {
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 4,
            InputFullMode = BoundedChannelFullMode.DropWrite,
            AdaptiveParallelism = new AdaptiveParallelismOptions
            {
                Enabled = false,
                MinConcurrency = 1,
                MaxConcurrency = 4,
                InitialConcurrency = 1,
            },
        };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    private static PipelineRuntimeOptions Options(
        ManualPipelineClock clock,
        int maxConcurrency,
        int initialConcurrency,
        int minConcurrency = 1,
        int adaptiveMaxConcurrency = 4) =>
        new()
        {
            MaxConcurrency = maxConcurrency,
            InputCapacity = global::System.Math.Max(4, maxConcurrency),
            Clock = clock,
            AdaptiveParallelism = new AdaptiveParallelismOptions
            {
                Enabled = true,
                MinConcurrency = minConcurrency,
                MaxConcurrency = adaptiveMaxConcurrency,
                InitialConcurrency = initialConcurrency,
                TargetLatency = TimeSpan.FromMilliseconds(100),
                DeadZone = TimeSpan.FromMilliseconds(5),
                Cooldown = TimeSpan.FromTicks(1),
                MaxAdjustmentStep = 1,
                FailurePressureThreshold = 0.10,
                MinSmoothingFactor = 1,
            },
        };

    private sealed class CountingEnvelopeSource : IPipelineSource<int>
    {
        private readonly ProcessingEnvelope<int>[] _items;
        private readonly int _emittedThreshold;
        private int _emittedCount;

        public CountingEnvelopeSource(params int[] payloads)
            : this(payloads.AsEnumerable())
        {
        }

        public CountingEnvelopeSource(IEnumerable<int> payloads, int emittedThreshold = 0)
        {
            _emittedThreshold = emittedThreshold;
            _items = payloads
                .Select((payload, index) =>
                    ProcessingEnvelope<int>.Create(
                        payload,
                        "adaptive-test-pipeline",
                        "adaptive-test-run",
                        (ulong)(index + 1)))
                .ToArray();
        }

        public TaskCompletionSource ThresholdReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                var emitted = Interlocked.Increment(ref _emittedCount);
                if (_emittedThreshold > 0 && emitted >= _emittedThreshold)
                    ThresholdReached.TrySetResult();

                yield return item;
                await Task.Yield();
            }

            ThresholdReached.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingTransformer : IPipelineTransformer<int, int>
    {
        private readonly ManualPipelineClock _clock;
        private readonly Func<int, TimeSpan> _latency;
        private readonly Func<int, bool> _shouldBlock;
        private readonly Func<int, bool> _shouldThrow;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _activeThresholds = [];
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _completedThresholds = [];
        private int _activeCalls;
        private int _completedCalls;
        private int _maxObservedConcurrency;
        private int _startedCalls;

        public TrackingTransformer(
            ManualPipelineClock clock,
            Func<int, TimeSpan> latency,
            Func<int, bool> shouldBlock,
            Func<int, bool>? shouldThrow = null)
        {
            _clock = clock;
            _latency = latency;
            _shouldBlock = shouldBlock;
            _shouldThrow = shouldThrow ?? (_ => false);
        }

        public int ActiveCalls => Volatile.Read(ref _activeCalls);

        public int CompletedCalls => Volatile.Read(ref _completedCalls);

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public int StartedCalls => Volatile.Read(ref _startedCalls);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            TrackMax(active);
            CompleteThresholds(_activeThresholds, active);
            Interlocked.Increment(ref _startedCalls);

            try
            {
                if (_shouldThrow(envelope.Payload))
                    throw new InvalidOperationException("stage failed");

                var elapsed = _latency(envelope.Payload);
                if (elapsed > TimeSpan.Zero)
                    _clock.Advance(elapsed);

                if (_shouldBlock(envelope.Payload))
                    await _release.Task.WaitAsync(ct).ConfigureAwait(false);

                var completed = Interlocked.Increment(ref _completedCalls);
                CompleteThresholds(_completedThresholds, completed);
                return StageResult<int>.Success(envelope.Payload);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task WaitForActiveAsync(int threshold) =>
            WaitForThresholdAsync(_activeThresholds, threshold, () => MaxObservedConcurrency);

        public Task WaitForCompletedAsync(int threshold) =>
            WaitForThresholdAsync(_completedThresholds, threshold, () => CompletedCalls);

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task WaitForThresholdAsync(
            ConcurrentDictionary<int, TaskCompletionSource> thresholds,
            int threshold,
            Func<int> getCurrent)
        {
            if (getCurrent() >= threshold)
                return Task.CompletedTask;

            var waiter = thresholds.GetOrAdd(
                threshold,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            if (getCurrent() >= threshold)
                waiter.TrySetResult();

            return waiter.Task;
        }

        private static void CompleteThresholds(
            ConcurrentDictionary<int, TaskCompletionSource> thresholds,
            int value)
        {
            foreach (var pair in thresholds)
            {
                if (value >= pair.Key)
                    pair.Value.TrySetResult();
            }
        }

        private void TrackMax(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxObservedConcurrency);
                if (active <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, active, current) == current)
                    return;
            }
        }
    }

    private enum PayloadGate
    {
        None,
        First,
        Second,
    }

    private sealed class PayloadGateTrackingTransformer : IPipelineTransformer<int, int>
    {
        private readonly ManualPipelineClock _clock;
        private readonly Func<int, TimeSpan> _latency;
        private readonly Func<int, PayloadGate> _gateSelector;
        private readonly TaskCompletionSource _firstGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _activeThresholds = [];
        private int _activeCalls;
        private int _maxObservedConcurrency;

        public PayloadGateTrackingTransformer(
            ManualPipelineClock clock,
            Func<int, TimeSpan> latency,
            Func<int, PayloadGate> gateSelector)
        {
            _clock = clock;
            _latency = latency;
            _gateSelector = gateSelector;
        }

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            TrackMax(active);
            CompleteThresholds(_activeThresholds, active);

            try
            {
                var elapsed = _latency(envelope.Payload);
                if (elapsed > TimeSpan.Zero)
                    _clock.Advance(elapsed);

                await WaitForPayloadGateAsync(envelope.Payload, ct).ConfigureAwait(false);
                return StageResult<int>.Success(envelope.Payload);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task WaitForActiveAsync(int threshold) =>
            WaitForThresholdAsync(_activeThresholds, threshold, () => MaxObservedConcurrency);

        public void ReleaseFirstGate() => _firstGate.TrySetResult();

        public void ReleaseSecondGate() => _secondGate.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task WaitForPayloadGateAsync(int payload, CancellationToken ct)
        {
            return _gateSelector(payload) switch
            {
                PayloadGate.First => _firstGate.Task.WaitAsync(ct),
                PayloadGate.Second => _secondGate.Task.WaitAsync(ct),
                _ => Task.CompletedTask,
            };
        }

        private static Task WaitForThresholdAsync(
            ConcurrentDictionary<int, TaskCompletionSource> thresholds,
            int threshold,
            Func<int> getCurrent)
        {
            if (getCurrent() >= threshold)
                return Task.CompletedTask;

            var waiter = thresholds.GetOrAdd(
                threshold,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            if (getCurrent() >= threshold)
                waiter.TrySetResult();

            return waiter.Task;
        }

        private static void CompleteThresholds(
            ConcurrentDictionary<int, TaskCompletionSource> thresholds,
            int value)
        {
            foreach (var pair in thresholds)
            {
                if (value >= pair.Key)
                    pair.Value.TrySetResult();
            }
        }

        private void TrackMax(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxObservedConcurrency);
                if (active <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, active, current) == current)
                    return;
            }
        }
    }

    private sealed class ManualPipelineClock : IPipelineClock
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public long GetTimestamp()
        {
            lock (_gate)
                return _now.UtcTicks;
        }

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan value)
        {
            lock (_gate)
                _now += value;
        }
    }
}
