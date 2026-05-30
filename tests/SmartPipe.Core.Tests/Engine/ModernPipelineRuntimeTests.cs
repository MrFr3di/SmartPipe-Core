using System.Globalization;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class ModernPipelineRuntimeTests
{
    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldRunTypedStagesThroughSingleOutput()
    {
        var source = new EnvelopeSource<int>(1, 2, 3);
        var sink = new EnvelopeCollectingSink<string>();

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, double>(x => x + 0.5))
            .Transform(
                new EnvelopeTransformer<double, string>(x =>
                    x.ToString(CultureInfo.InvariantCulture)
                )
            )
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().Equal("1.5", "2.5", "3.5");
        outputs.Should().OnlyContain(x => x.Envelope != null);
        sink.Payloads.Should().Equal("1.5", "2.5", "3.5");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldPreserveTraceAndMetadata()
    {
        var source = new EnvelopeSource<int>(
            new ProcessingEnvelope<int>
            {
                PipelineId = "input-pipeline",
                RunId = "input-run",
                TraceId = 42,
                Payload = 10,
                Metadata = MetadataBag.Empty.Set("tenant", "alpha"),
                Lineage = [],
                Attempt = 2,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, string>(x => $"value:{x}"))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        var envelope = outputs.Single().Envelope;
        envelope.Should().NotBeNull();
        envelope!.TraceId.Should().Be(42);
        envelope.Attempt.Should().Be(2);
        envelope.Metadata.GetString("tenant").Should().Be("alpha");
        envelope.Lineage.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldFaultCompletion_WhenTransformerReturnsInvalidStageResult()
    {
        var source = new EnvelopeSource<int>(1);
        var sink = new EnvelopeCollectingSink<string>();

        var run = PipelineBuilder
            .From(source)
            .Transform(new InvalidStageTransformer<int, string>())
            .To(sink);

        var act = async () => await run.Completion;

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*default(StageResult<T>)*");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldDisposeRuntimeOwnedComponentsOnce()
    {
        var source = new CountingEnvelopeSource<int>(1);
        var transformer = new CountingEnvelopeTransformer<int, string>(x =>
            x.ToString(CultureInfo.InvariantCulture)
        );
        var sink = new CountingEnvelopeSink<string>();

        var run = PipelineBuilder.From(source).Transform(transformer).To(sink);

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        await run.DisposeAsync();

        source.DisposeCount.Should().Be(1);
        transformer.DisposeCount.Should().Be(1);
        sink.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldRejectSecondRunForSingleUseInstances()
    {
        var builder = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            );

        var firstRun = builder.Run();
        _ = await ReadOutputsAsync(firstRun.Outputs);
        await firstRun.Completion;

        var act = () => builder.Run();

        act.Should().Throw<InvalidOperationException>().WithMessage("*single-use*");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldAllowRepeatedRunsForFactoryDefinitions()
    {
        var sourceCreated = 0;
        var firstStageCreated = 0;
        var secondStageCreated = 0;
        var sinks = new List<EnvelopeCollectingSink<string>>();

        var builder = PipelineBuilder
            .FromFactory<int>(_ =>
            {
                sourceCreated++;
                return new EnvelopeSource<int>(1, 2);
            })
            .TransformFactory<double>(_ =>
            {
                firstStageCreated++;
                return new EnvelopeTransformer<int, double>(x => x + 0.5);
            })
            .TransformFactory<string>(_ =>
            {
                secondStageCreated++;
                return new EnvelopeTransformer<double, string>(x =>
                    x.ToString(CultureInfo.InvariantCulture)
                );
            });

        var firstRun = builder.ToFactory(_ => CreateSink(sinks));
        var firstOutputs = await ReadOutputsAsync(firstRun.Outputs);
        await firstRun.Completion;

        var secondRun = builder.ToFactory(_ => CreateSink(sinks));
        var secondOutputs = await ReadOutputsAsync(secondRun.Outputs);
        await secondRun.Completion;

        sourceCreated.Should().Be(2);
        firstStageCreated.Should().Be(2);
        secondStageCreated.Should().Be(2);
        sinks.Should().HaveCount(2);
        firstOutputs.Select(x => x.Result.Value).Should().Equal("1.5", "2.5");
        secondOutputs.Select(x => x.Result.Value).Should().Equal("1.5", "2.5");
        sinks[0].Payloads.Should().Equal("1.5", "2.5");
        sinks[1].Payloads.Should().Equal("1.5", "2.5");

        static EnvelopeCollectingSink<string> CreateSink(
            List<EnvelopeCollectingSink<string>> created
        )
        {
            var sink = new EnvelopeCollectingSink<string>();
            created.Add(sink);
            return sink;
        }
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldAllowFactoryDefinitionRunWithoutSink()
    {
        var sourceCreated = 0;
        var stageCreated = 0;

        var builder = PipelineBuilder
            .FromFactory<int>(_ =>
            {
                sourceCreated++;
                return new EnvelopeSource<int>(7);
            })
            .TransformFactory<string>(_ =>
            {
                stageCreated++;
                return new EnvelopeTransformer<int, string>(x =>
                    x.ToString(CultureInfo.InvariantCulture)
                );
            });

        var run = builder.Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        sourceCreated.Should().Be(1);
        stageCreated.Should().Be(1);
        outputs.Single().Result.Value.Should().Be("7");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitLifecycleEventsToObservers()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.Should().ContainSingle(e => e is PipelineStartedEvent);
        observer.Events.Should().ContainSingle(e => e is PipelineCompletedEvent);
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitStageAndSinkEvents()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, double>(x => x + 0.5))
            .Transform(
                new EnvelopeTransformer<double, string>(x =>
                    x.ToString(CultureInfo.InvariantCulture)
                )
            )
            .WithObserver(observer)
            .To(new EnvelopeCollectingSink<string>());

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<StageStartedEvent>().Should().HaveCount(2);
        observer.Events.OfType<StageSucceededEvent>().Should().HaveCount(2);
        observer.Events.OfType<SinkWriteStartedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitStageFailedEvent()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new FailingStageTransformer<int, string>())
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.IsSuccess.Should().BeFalse();
        var failure = observer.Events.OfType<StageFailedEvent>().Single();
        failure.TraceId.Should().NotBe(0);
        failure.StageId.Should().Be("stage-1");
        failure.Error.Category.Should().Be("TestFailure");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldWriteDeadLetterEnvelope_WhenStagePolicyRequiresDeadLetter()
    {
        await using var stream = new MemoryStream();
        var serializer = new JsonLinesDeadLetterSerializer<int>();
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(42))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream, serializer)
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.IsSuccess.Should().BeFalse();
        observer.Events.OfType<DeadLetterWrittenEvent>().Should().ContainSingle();

        stream.Position = 0;
        var deadLetters = new List<DeadLetterEnvelope<int>>();
        await foreach (var envelope in serializer.ReadAsync(stream))
            deadLetters.Add(envelope);

        var deadLetter = deadLetters.Single();
        deadLetter.OriginalPayload.Should().Be(42);
        deadLetter.StageId.Should().Be("stage-1");
        deadLetter.Error.Category.Should().Be("TestFailure");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldSkipFailedItem_WhenStagePolicyRequiresSkip()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.Skip }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
        observer.Events.OfType<StageFailedEvent>().Should().HaveCount(2);
        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldFaultRun_WhenStagePolicyRequiresFaultPipeline()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.FaultPipeline }
            )
            .WithObserver(observer)
            .Run();

        var act = async () => await run.Completion;

        await act.Should()
            .ThrowAsync<PipelineFailureActionException>()
            .WithMessage("*Test failure*");
        observer.Events.OfType<PipelineFaultedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldStopReading_WhenStagePolicyRequiresStopPipeline()
    {
        var source = new CountingEnvelopeSource<int>(1, 2, 3);
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(source)
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.StopPipeline }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.IsSuccess.Should().BeFalse();
        source.ItemsYielded.Should().Be(1);
        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitTimedOutFailure_WhenAttemptTimeoutExpires()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new SlowStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy { AttemptTimeout = TimeSpan.FromMilliseconds(20) },
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        var result = outputs.Single().Result;
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Timeout");
        observer.Events.OfType<StageFailedEvent>().Single().Error.Category.Should().Be("Timeout");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitTimedOutFailure_WhenStageTimeoutExpires()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new SlowStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(20) },
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        var result = outputs.Single().Result;
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Timeout");
        observer.Events.OfType<StageFailedEvent>().Single().Error.Category.Should().Be("Timeout");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldRetryTransientStageFailureBeforeTerminalAction()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 1,
            value => value.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.Value.Should().Be("7");
        transformer.Calls.Should().Be(2);
        observer.Events.OfType<RetryScheduledEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryAttemptedEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldApplyRetryExhaustedAction_WhenRetryBudgetEnds()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 10,
            value => value.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().BeEmpty();
        transformer.Calls.Should().Be(2);
        observer.Events.OfType<RetryScheduledEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryAttemptedEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitPipelineFaultedEvent_WhenStageThrows()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ThrowingStageTransformer<int, string>())
            .WithObserver(observer)
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stage boom*");
        observer.Events.OfType<PipelineFaultedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitSinkWriteFailedEvent_WhenSinkThrows()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            )
            .WithObserver(observer)
            .To(new ThrowingEnvelopeSink<string>());

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sink boom*");
        observer.Events.OfType<SinkWriteFailedEvent>().Should().ContainSingle();
        observer.Events.OfType<PipelineFaultedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitPipelineCancelledEvent_WhenRunIsCancelled()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new WaitingEnvelopeSource<int>())
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            )
            .WithObserver(observer)
            .Run();

        await run.CancelAsync();
        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<OperationCanceledException>();
        observer.Events.OfType<PipelineCancelledEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldFaultRun_WhenCriticalObserverFails()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            )
            .WithObserver(
                new ThrowingObserver(),
                ObserverReliability.Critical,
                ObserverFailurePolicy.FaultPipeline
            )
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*observer boom*");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldEmitObserverFailedEvent_ForBestEffortObserverFailure()
    {
        var recording = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            )
            .WithObserver(new ThrowingObserver())
            .WithObserver(recording)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        recording.Events.OfType<ObserverFailedEvent>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RetryDelayLongerThanRemainingStageTimeout_IsNotWaited()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 10,
            value => value.ToString(CultureInfo.InvariantCulture)
        );
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromSeconds(5)),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(50) },
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        outputs.Should().BeEmpty();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
        observer.Events.OfType<StageFailedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task RetryIsNotScheduled_WhenDelayCannotFitIntoRemainingStageBudget()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 10,
            value => value.ToString(CultureInfo.InvariantCulture)
        );
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromSeconds(5)),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(50) },
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        outputs.Should().BeEmpty();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task RetryExhaustedEvent_IsEmitted_WhenRetryBudgetExhaustedByStageTimeout()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 10,
            value => value.ToString(CultureInfo.InvariantCulture)
        );
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMilliseconds(60)),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(120) },
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        outputs.Should().BeEmpty();
        observer.Events.OfType<RetryScheduledEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryAttemptedEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task OnRetryExhausted_IsAppliedExactlyOnce_WhenBudgetExhausted()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 10,
            value => value.ToString(CultureInfo.InvariantCulture)
        );
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMilliseconds(60)),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(120) },
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        outputs.Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
        observer.Events.OfType<DeadLetterWrittenEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task RetryStillSucceeds_WhenDelayAndNextAttemptFitInsideStageTimeout()
    {
        var observer = new RecordingObserver();
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 1,
            value => value.ToString(CultureInfo.InvariantCulture)
        );
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(7))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.FromMilliseconds(50)),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(200) },
                }
            )
            .WithObserver(observer)
            .Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        outputs.Single().Result.Value.Should().Be("7");
        observer.Events.OfType<RetryScheduledEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryAttemptedEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task TimeoutAttemptThatExhaustsStageBudget_DoesNotScheduleRetry()
    {
        var observer = new RecordingObserver();
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new SlowStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(30) },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                }
            )
            .WithObserver(observer)
            .Run();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        var result = outputs.Single().Result;
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Timeout");
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader
    )
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);
        return outputs;
    }

    private sealed class EnvelopeSource<T> : IPipelineSource<T>
    {
        private readonly ProcessingEnvelope<T>[] _items;

        public EnvelopeSource(params T[] payloads)
        {
            _items = payloads
                .Select(payload => new ProcessingEnvelope<T>
                {
                    PipelineId = "test-pipeline",
                    RunId = "test-run",
                    TraceId = (ulong)Random.Shared.Next(1, int.MaxValue),
                    Payload = payload,
                    Metadata = MetadataBag.Empty,
                    Lineage = [],
                    Attempt = 0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                })
                .ToArray();
        }

        public EnvelopeSource(params ProcessingEnvelope<T>[] items)
        {
            _items = items;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitingEnvelopeSource<T> : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EnvelopeTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly Func<TInput, TOutput> _transform;

        public EnvelopeTransformer(Func<TInput, TOutput> transform)
        {
            _transform = transform;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            return ValueTask.FromResult(StageResult<TOutput>.Success(_transform(envelope.Payload)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InvalidStageTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            return ValueTask.FromResult(default(StageResult<TOutput>));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EnvelopeCollectingSink<T> : IPipelineSink<T>
    {
        private readonly List<T> _payloads = [];

        public IReadOnlyList<T> Payloads => _payloads;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
        {
            _payloads.Add(envelope.Payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingEnvelopeSource<T> : IPipelineSource<T>
    {
        private readonly EnvelopeSource<T> _inner;

        public CountingEnvelopeSource(params T[] payloads)
        {
            _inner = new EnvelopeSource<T>(payloads);
        }

        public int DisposeCount { get; private set; }

        public int ItemsYielded { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await foreach (var envelope in _inner.ReadEnvelopesAsync(ct))
            {
                ItemsYielded++;
                yield return envelope;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingEnvelopeTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly EnvelopeTransformer<TInput, TOutput> _inner;

        public CountingEnvelopeTransformer(Func<TInput, TOutput> transform)
        {
            _inner = new EnvelopeTransformer<TInput, TOutput>(transform);
        }

        public int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        ) => _inner.TransformAsync(envelope, ct);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingEnvelopeSink<T> : IPipelineSink<T>
    {
        public int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default
        ) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStageTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            var error = new SmartPipeError("Test failure", ErrorType.Permanent, "TestFailure");
            return ValueTask.FromResult(StageResult<TOutput>.Failure(error));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingStageTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            throw new InvalidOperationException("stage boom");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SlowStageTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return StageResult<TOutput>.Success(default!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FlakyStageTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly int _failuresBeforeSuccess;
        private readonly Func<TInput, TOutput> _success;

        public FlakyStageTransformer(int failuresBeforeSuccess, Func<TInput, TOutput> success)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _success = success;
        }

        public int Calls { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            Calls++;
            if (Calls <= _failuresBeforeSuccess)
            {
                var error = new SmartPipeError(
                    "Transient test failure",
                    ErrorType.Transient,
                    "Retryable"
                );
                return ValueTask.FromResult(StageResult<TOutput>.Failure(error));
            }

            return ValueTask.FromResult(StageResult<TOutput>.Success(_success(envelope.Payload)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingEnvelopeSink<T> : IPipelineSink<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
        {
            throw new InvalidOperationException("sink boom");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        private readonly List<PipelineEvent> _events = [];

        public IReadOnlyList<PipelineEvent> Events => _events;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _events.Add(pipelineEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IPipelineObserver
    {
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            throw new InvalidOperationException("observer boom");
        }
    }

    #region Circuit Breaker Tests

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldRunWithoutCircuitBreaker_WhenPolicyNotConfigured()
    {
        var source = new EnvelopeSource<int>(1, 2, 3);
        var sink = new EnvelopeCollectingSink<string>();

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, double>(x => x + 0.5))
            .Transform(
                new EnvelopeTransformer<double, string>(x =>
                    x.ToString(CultureInfo.InvariantCulture)
                )
            )
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().Equal("1.5", "2.5", "3.5");
        outputs.Should().OnlyContain(x => x.Envelope != null);
        sink.Payloads.Should().Equal("1.5", "2.5", "3.5");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_ShouldCreateIndependentCircuitBreakers_PerStage()
    {
        var observer = new RecordingObserver();
        var stage1Transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 100, // Always fails
            x => x.ToString(CultureInfo.InvariantCulture)
        );
        var stage2Transformer = new FlakyStageTransformer<string, int>(
            failuresBeforeSuccess: 0, // Always succeeds
            x => int.Parse(x)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                stage1Transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 2,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .Transform(
                stage2Transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 5,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Stage 1 fails and skips items; stage 2 is never reached
        outputs.Should().BeEmpty();
        stage2Transformer.Calls.Should().Be(0);
        observer.Events.Should().NotContain(e => e.StageId == "stage-2");
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldRejectWithoutCallingTransformer_WhenOpen()
    {
        var observer = new RecordingObserver();
        var failingTransformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 999, // Always fails
            x => x.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(
                failingTransformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 3,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // First 3 items fail and open the breaker
        // 4th item is rejected without calling transformer
        failingTransformer.Calls.Should().Be(3); // Only 3 calls, not 4
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldApplyPermanentFailurePolicy_WhenOpen()
    {
        var observer = new RecordingObserver();
        var failingTransformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 999, // Always fails
            x => x.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(
                failingTransformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 3,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Items skipped, no outputs
        outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldEmitRejectedEvent_WhenOpen()
    {
        var observer = new RecordingObserver();
        var failingTransformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 999,
            x => x.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(
                failingTransformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 3,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().HaveCount(1); // Only 4th item rejected
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldOpenAfterConfiguredFailedAttempts()
    {
        var observer = new RecordingObserver();
        var failingTransformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 999,
            x => x.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4, 5))
            .Transform(
                failingTransformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 3,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // First 3 failures open the breaker
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        // Items 4 and 5 are rejected
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldRecordSuccess_OnSuccessfulAttempt()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 5,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // All items succeed, no breaker opens
        outputs.Select(x => x.Result.Value).Should().Equal("1", "2", "3", "4");
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().BeEmpty();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldRecordFailedRetryAttempts_WhenRetryConfigured()
    {
        var observer = new RecordingObserver();

        // Failing transformer that returns Transient errors (retryable by default).
        // FlakyStageTransformer with high failuresBeforeSuccess: always fails with Transient errors.
        var transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 999,
            x => x.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 3, // 3 failed attempts open the breaker
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    Retry = new RetryPolicy(maxRetries: 2, delay: TimeSpan.Zero),
                    OnPermanentFailure = FailureAction.Skip,
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Item 1: 3 attempts (initial + 2 retries), all fail with Transient errors.
        // Breaker opens on 3rd failure. Item 1 exhausted and skipped.
        // Item 2: rejected by open breaker.
        // If only terminal results were counted, breaker would not open (only 1 exhaustion from item 1).
        // The fact it opens proves per-attempt (including retry) failures are recorded.
        outputs.Should().BeEmpty();
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle(); // Item 2
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldNotRetryRejectedOpenBreaker()
    {
        var observer = new RecordingObserver();
        var failingTransformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 999,
            x => x.ToString(CultureInfo.InvariantCulture)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(
                failingTransformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 2, // Open after 2 failures
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Item 1 fails (1 failure), retry fails (2 failures → breaker opens on 2nd failure)
        // Item 1 exhausted and skipped. Items 2-4 rejected without retry.
        // RetryScheduledEvent should only appear for item 1 (1 retry), not for rejected items 2-4
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().HaveCount(3); // Items 2, 3, 4
        observer.Events.OfType<RetryScheduledEvent>().Should().HaveCount(1); // Only item 1 had a retry
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldTreatAttemptTimeoutAsFailure()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .Transform(
                new SlowStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy { AttemptTimeout = TimeSpan.FromMilliseconds(20) },
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 2,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // First 2 timeouts open breaker, 3rd is rejected
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreakerRejectedItems_ShouldNotHangRunCompletion()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4, 5))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 2,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion; // Should complete without hang

        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    #endregion
}
