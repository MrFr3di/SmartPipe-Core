#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
public sealed class RuntimeHardeningTests
{
    [Theory]
    [InlineData(-2)]
    [InlineData(-1000)]
    public async Task TryDrainAsync_NegativeTimeout_ThrowsWithoutRequestingDrain(int milliseconds)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("drain-validation"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(new BlockingSource(release.Task))))
            .Build();
        await using var run = await definition.StartAsync(TestContext.Current.CancellationToken);

        var tryDrain = () => run.TryDrainAsync(TimeSpan.FromMilliseconds(milliseconds)).AsTask();
        var drain = () => run.DrainAsync(TimeSpan.FromMilliseconds(milliseconds)).AsTask();

        (await tryDrain.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("timeout");
        (await drain.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("timeout");
        run.State.Should().Be(PipelineRunState.Running);

        release.SetResult();
        var result = await run.TryDrainAsync(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
        result.Status.Should().BeOneOf(PipelineDrainStatus.Completed, PipelineDrainStatus.AlreadyCompleted);
    }

    [Fact]
    public async Task TryDrainAsync_TimeoutAboveTimerLimit_Throws()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        var act = () => run.TryDrainAsync(TimeSpan.FromDays(60)).AsTask();

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await run.DisposeAsync();
    }

    [Fact]
    public async Task StageExecutor_StartEventFailure_ReleasesHalfOpenProbeSlot()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                MinimumThroughput = 1,
                BreakDuration = TimeSpan.FromSeconds(10),
                MaxHalfOpenRequests = 1,
            },
            time);
        breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Open);
        time.Advance(TimeSpan.FromSeconds(11));

        var stage = new TypedPipelineStage<int, int>(new EnvelopeTransformer<int, int>(x => x), 0);
        var clock = SystemPipelineClock.Instance;
        var executor = new StageExecutor(
            "pipeline",
            "run",
            LineageMode.Off,
            clock,
            new PipelineTime(clock),
            _ => breaker,
            (_, _, _, _) => new RetryDecision(RetryDecisionKind.NotRetryable, 0, TimeSpan.Zero),
            (_, _, _, _, _, _) => ValueTask.CompletedTask,
            (_, _, _, _) => ValueTask.CompletedTask,
            (_, _, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            (pipelineEvent, _) => pipelineEvent is StageStartedEvent
                ? ValueTask.FromException(new InvalidOperationException("observer failed"))
                : ValueTask.CompletedTask,
            (_, _, _, _) => throw new InvalidOperationException("stage must not run"));

        var act = () => executor
            .ExecuteAsync(stage, ProcessingEnvelope<int>.Create(1), CancellationToken.None)
            .AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("observer failed");
        breaker.State.Should().Be(CircuitState.HalfOpen);
        using var next = breaker.AcquirePermit();
        next.IsAllowed.Should().BeTrue("the failed attempt must return its half-open probe slot");
    }

    [Fact]
    public async Task Lineage_LongStageChain_RecordsEveryStageInOrder()
    {
        const int stageCount = 40;
        var builder = PipelineDefinitionBuilder.From(
                new PipelineKey("lineage-chain"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(new EnvelopeSource<int>(7))))
            .WithLineageMode(LineageMode.Minimal)
            .Transform(
                new PipelineStageKey("stage-0"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(new EnvelopeTransformer<int, int>(x => x + 1))));
        for (var index = 1; index < stageCount; index++)
        {
            builder = builder.Transform(
                new PipelineStageKey($"stage-{index}"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(new EnvelopeTransformer<int, int>(x => x + 1))));
        }

        await using var run = await builder.Build().StartAsync(TestContext.Current.CancellationToken);
        var outputs = new List<PipelineOutput<int>>();
        await foreach (var output in run.Outputs.ReadAllAsync(TestContext.Current.CancellationToken))
            outputs.Add(output);
        await run.Completion;

        var envelope = outputs.Should().ContainSingle().Which.Envelope;
        envelope.Should().NotBeNull();
        envelope!.Payload.Should().Be(7 + stageCount);
        envelope.Lineage.Select(entry => entry.StageId)
            .Should().Equal(Enumerable.Range(0, stageCount).Select(index => $"stage-{index}"));
    }

    [Fact]
    public void LineageTrail_AppendsFromSharedPrefix_KeepEachViewImmutable()
    {
        var first = Entry("a");
        var root = LineageTrail.Append([], first);
        var left = LineageTrail.Append(root, Entry("left"));
        var right = LineageTrail.Append(root, Entry("right"));
        var leftChild = LineageTrail.Append(left, Entry("left-child"));

        root.Select(entry => entry.StageId).Should().Equal("a");
        left.Select(entry => entry.StageId).Should().Equal("a", "left");
        right.Select(entry => entry.StageId).Should().Equal("a", "right");
        leftChild.Select(entry => entry.StageId).Should().Equal("a", "left", "left-child");
        left.Count.Should().Be(2);
        right[1].StageId.Should().Be("right");
        var outOfRange = () => left[2];
        outOfRange.Should().Throw<ArgumentOutOfRangeException>();

        var fromArray = LineageTrail.Append([first, Entry("b")], Entry("c"));
        fromArray.Select(entry => entry.StageId).Should().Equal("a", "b", "c");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LineageTrail_ConcurrentBranchesFromSharedPrefix_NeverObserveEachOther(int prefixLength)
    {
        // Prefix lengths 3 and 4 put the contended append just before and exactly at the capacity edge.
        IReadOnlyList<LineageEntry> prefix = [];
        for (var index = 0; index < prefixLength; index++)
            prefix = LineageTrail.Append(prefix, Entry($"p{index}"));
        var expectedPrefix = prefix.Select(entry => entry.StageId).ToArray();

        for (var round = 0; round < 200; round++)
        {
            using var start = new Barrier(8);
            var branches = await Task.WhenAll(Enumerable.Range(0, 8).Select(branch => Task.Run(() =>
            {
                start.SignalAndWait();
                var first = LineageTrail.Append(prefix, Entry($"b{branch}"));
                return (Branch: branch, Trail: LineageTrail.Append(first, Entry($"b{branch}-next")));
            })));

            foreach (var (branch, trail) in branches)
            {
                trail.Select(entry => entry.StageId)
                    .Should().Equal(expectedPrefix.Concat([$"b{branch}", $"b{branch}-next"]));
            }

            prefix.Select(entry => entry.StageId).Should().Equal(expectedPrefix);
        }
    }

    [Fact]
    public async Task Metrics_CanonicalPipeline_TagsMeasurementsWithPipelineId()
    {
        var pipelineId = $"metrics-{Guid.NewGuid():N}"[..20];
        var measurements = new ConcurrentQueue<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == SmartPipeMeter.Name)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey(pipelineId),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(new EnvelopeSource<int>(1, 2))))
            .Transform(
                new PipelineStageKey("identity"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(new EnvelopeTransformer<int, int>(x => x))))
            .Build();
        await using (var run = await definition.StartAsync(TestContext.Current.CancellationToken))
        {
            await foreach (var _ in run.Outputs.ReadAllAsync(TestContext.Current.CancellationToken))
            {
            }

            await run.Completion;
        }

        var tagged = measurements
            .Where(measurement => measurement.Tags.Contains(
                new KeyValuePair<string, object?>("smartpipe.pipeline_id", pipelineId)))
            .ToArray();
        tagged.Where(measurement => measurement.Instrument == "smartpipe.items.processed")
            .Should().HaveCount(2);
        tagged.Should().Contain(measurement => measurement.Instrument == "smartpipe.stage.duration");
    }

    [Fact]
    public void Metrics_RecorderWithoutPipelineId_EmitsUntaggedMeasurements()
    {
        var tags = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        var recorder = new SmartPipeMetricsRecorder(SystemPipelineClock.Instance, pipelineId: null);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == SmartPipeMeter.Name && instrument.Name == "smartpipe.items.retried")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, measurementTags, _) =>
            tags.Enqueue(measurementTags.ToArray()));
        listener.Start();

        recorder.RecordRetry();

        tags.Should().Contain(measurementTags => measurementTags.Length == 0);
        tags.SelectMany(measurementTags => measurementTags)
            .Should().NotContain(tag => tag.Key == "smartpipe.pipeline_id" && tag.Value == null);
    }

    private static LineageEntry Entry(string stageId) =>
        new(stageId, stageId, "System.Int32", "System.Int32", DateTimeOffset.UnixEpoch, null, StageOutcome.Succeeded);

    private sealed class BlockingSource(Task release) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await release.WaitAsync(ct);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
