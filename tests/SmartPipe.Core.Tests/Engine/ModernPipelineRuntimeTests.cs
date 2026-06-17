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
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
            .To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(x => x.Result.Value).Should().Equal("1.5", "2.5", "3.5");
        outputs.Should().OnlyContain(x => x.Envelope != null);
        sink.Payloads.Should().Equal("1.5", "2.5", "3.5");
    }

    [Fact]
    public async Task TypedRuntime_ComponentSplit_BasicSourceTransformSinkStillWorks()
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
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
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
        envelope.Attempt.Should().Be(0);
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
    public async Task PipelineBuilder_ModernApi_SourceInitializeFailure_FaultsRunAndDisposesSource()
    {
        var source = new ThrowingInitializeEnvelopeSource<int>("source init boom");

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("source init boom");
        run.State.Should().Be(PipelineRunState.Faulted);
        run.Outputs.Completion.IsFaulted.Should().BeTrue();
        source.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_SinkInitializeFailure_FaultsRunAndDisposesSink()
    {
        var sink = new ThrowingInitializeEnvelopeSink<int>("sink init boom");

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .To(sink);

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sink init boom");
        run.State.Should().Be(PipelineRunState.Faulted);
        run.Outputs.Completion.IsFaulted.Should().BeTrue();
        sink.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task TypedRuntime_ShouldDisposeRuntimeOwnedSourceTransformerSink_OnSuccess()
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
    public async Task TypedRuntime_ShouldDisposeRuntimeOwnedComponents_OnTerminalFailure()
    {
        var source = new CountingEnvelopeSource<int>(1);
        var transformer = new CountingThrowingEnvelopeTransformer<int, string>();
        var sink = new CountingEnvelopeSink<string>();

        var run = PipelineBuilder.From(source).Transform(transformer).To(sink);

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Category.Should().Be("StageException");
        source.DisposeCount.Should().Be(1);
        transformer.DisposeCount.Should().Be(1);
        sink.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task TypedRuntime_ShouldNotDisposeSingletonExternalComponents()
    {
        var source = new CountingEnvelopeSource<int>(
            PipelineComponentLifetime.SingletonExternal,
            1
        );
        var transformer = new CountingEnvelopeTransformer<int, string>(
            x => x.ToString(CultureInfo.InvariantCulture),
            PipelineComponentLifetime.SingletonExternal
        );
        var sink = new CountingEnvelopeSink<string>(PipelineComponentLifetime.SingletonExternal);

        var run = PipelineBuilder.From(source).Transform(transformer).To(sink);

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        await run.DisposeAsync();

        source.DisposeCount.Should().Be(0);
        transformer.DisposeCount.Should().Be(0);
        sink.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task TypedRuntime_ShouldDisposeBufferedObserverDispatcherOnce()
    {
        var observer = new RecordingObserver();
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture))
            )
            .WithObserver(observer)
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 16,
                        FlushOnCompletion = true,
                    },
                }
            )
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        await run.DisposeAsync();
        await run.DisposeAsync();

        observer.Events.OfType<PipelineStartedEvent>().Should().ContainSingle();
        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task TypedRuntime_ShouldDisposeOutputsAndCompleteRun_OnTerminalFailure()
    {
        var source = new CountingEnvelopeSource<int>(1);
        var transformer = new CountingThrowingEnvelopeTransformer<int, string>();

        var run = PipelineBuilder.From(source).Transform(transformer).Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Category.Should().Be("StageException");
        source.DisposeCount.Should().Be(1);
        transformer.DisposeCount.Should().Be(1);
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
            })
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
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
    public void PipelineBuilder_InstancePipelineTransformFactory_ShouldThrowClearError()
    {
        var builder = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)));

        var act = () => builder.TransformFactory<double>(_ =>
            new EnvelopeTransformer<string, double>(x => double.Parse(x, CultureInfo.InvariantCulture)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TransformFactory*FromFactory*");
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
    public async Task PipelineBuilder_ModernApi_DeadLetterWriteFailure_FaultsRunWithoutWrittenEvent()
    {
        await using var stream = new MemoryStream();
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(42))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream, new ThrowingDeadLetterSerializer<int>())
            )
            .WithObserver(observer)
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("dead-letter boom");
        run.State.Should().Be(PipelineRunState.Faulted);
        run.Outputs.Completion.IsFaulted.Should().BeTrue();
        observer.Events.OfType<DeadLetterWrittenEvent>().Should().BeEmpty();
        observer.Events.OfType<PipelineFaultedEvent>().Should().ContainSingle();
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
    public async Task PipelineBuilder_ModernApi_ShouldEmitStageFailedEvent_WhenStageThrows()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new ThrowingStageTransformer<int, string>())
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        observer.Events.OfType<StageFailedEvent>().Should().ContainSingle()
            .Which.Error.Category.Should().Be("StageException");
        observer.Events.OfType<PipelineFaultedEvent>().Should().BeEmpty();
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
    public async Task RetryAttempt_ShouldResetForNextStage_WhenPreviousStageSucceededAfterRetry()
    {
        var observer = new RecordingObserver();
        var stage1Transformer = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 1,
            value => value.ToString(CultureInfo.InvariantCulture)
        );
        var stage2Transformer = new EnvelopeTransformer<string, string>(x => $"final:{x}");

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(99))
            .Transform(
                stage1Transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                }
            )
            .Transform(stage2Transformer)
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.Value.Should().Be("final:99");
        // Stage 1 retried once, then succeeded.
        observer.Events.OfType<RetryScheduledEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryAttemptedEvent>().Should().ContainSingle();
        // Stage 2 started with Attempt=0, not inherited from stage 1.
        var stage2Started = observer.Events.OfType<StageStartedEvent>().Single(e => e.StageId == "stage-2");
        stage2Started.Attempt.Should().Be(0);
    }

    [Fact]
    public async Task RetryBudget_ShouldBePerStage()
    {
        var observer = new RecordingObserver();
        var stage1 = new FlakyStageTransformer<int, string>(
            failuresBeforeSuccess: 10, // Always fails
            x => x.ToString(CultureInfo.InvariantCulture)
        );
        var stage2 = new FlakyStageTransformer<string, int>(
            failuresBeforeSuccess: 1, // Succeeds after 1 retry
            x => int.Parse(x)
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(42))
            .Transform(
                stage1,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .Transform(
                stage2,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(maxRetries: 1, delay: TimeSpan.Zero),
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // Stage 1 exhausted its retry budget and was skipped.
        // Stage 2 was never reached because stage 1's skip terminates the item.
        outputs.Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
        // Verify stage 1 had its own independent attempt counting.
        var stage1Attempts = observer.Events
            .OfType<StageStartedEvent>()
            .Where(e => e.StageId == "stage-1")
            .Select(e => e.Attempt)
            .ToList();
        stage1Attempts.Should().Equal(0, 1); // Initial = 0, first retry = 1
        // Stage 2 was never started because stage 1 was skipped.
        observer.Events.OfType<StageStartedEvent>().Select(e => e.StageId).Should().NotContain("stage-2");
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

    private sealed class ThrowingInitializeEnvelopeSource<T> : IPipelineSource<T>
    {
        private readonly string _message;

        public ThrowingInitializeEnvelopeSource(string message)
        {
            _message = message;
        }

        public int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(_message);

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingEnvelopeSource<T> : IPipelineSource<T>, IPipelineComponentDescriptor
    {
        private readonly EnvelopeSource<T> _inner;

        public CountingEnvelopeSource(params T[] payloads)
            : this(PipelineComponentLifetime.SingleUse, payloads) { }

        public CountingEnvelopeSource(PipelineComponentLifetime lifetime, params T[] payloads)
        {
            _inner = new EnvelopeSource<T>(payloads);
            Lifetime = lifetime;
        }

        public PipelineComponentLifetime Lifetime { get; }

        public bool OwnsResources => Lifetime != PipelineComponentLifetime.SingletonExternal;

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
        : IPipelineTransformer<TInput, TOutput>,
            IPipelineComponentDescriptor
    {
        private readonly EnvelopeTransformer<TInput, TOutput> _inner;

        public CountingEnvelopeTransformer(Func<TInput, TOutput> transform)
            : this(transform, PipelineComponentLifetime.SingleUse) { }

        public CountingEnvelopeTransformer(
            Func<TInput, TOutput> transform,
            PipelineComponentLifetime lifetime
        )
        {
            _inner = new EnvelopeTransformer<TInput, TOutput>(transform);
            Lifetime = lifetime;
        }

        public PipelineComponentLifetime Lifetime { get; }

        public bool OwnsResources => Lifetime != PipelineComponentLifetime.SingletonExternal;

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

    private sealed class CountingThrowingEnvelopeTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        public int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            throw new InvalidOperationException("stage boom");
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingEnvelopeSink<T> : IPipelineSink<T>, IPipelineComponentDescriptor
    {
        public CountingEnvelopeSink()
            : this(PipelineComponentLifetime.SingleUse) { }

        public CountingEnvelopeSink(PipelineComponentLifetime lifetime)
        {
            Lifetime = lifetime;
        }

        public PipelineComponentLifetime Lifetime { get; }

        public bool OwnsResources => Lifetime != PipelineComponentLifetime.SingletonExternal;

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

    private sealed class ThrowingInitializeEnvelopeSink<T> : IPipelineSink<T>
    {
        private readonly string _message;

        public ThrowingInitializeEnvelopeSink(string message)
        {
            _message = message;
        }

        public int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(_message);

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDeadLetterSerializer<T> : IDeadLetterSerializer<T>
    {
        public ValueTask WriteAsync(
            DeadLetterEnvelope<T> envelope,
            Stream stream,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("dead-letter boom");

        public async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
            Stream stream,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }
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
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.EmitAll,
            })
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
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldApplyRetryExhaustedPolicy_WhenOpen()
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
                    OnRetryExhausted = FailureAction.Skip,
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
    public async Task TypedCircuitBreaker_RejectionErrorType_ShouldBeTransient()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerRejectedEvent>()
            .Single()
            .Error.Type.Should()
            .Be(ErrorType.Transient);
    }

    [Fact]
    public async Task TypedCircuitBreaker_Rejection_ShouldNotInvokeOnPermanentFailure()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new FlakyStageTransformer<int, string>(
                    failuresBeforeSuccess: 999,
                    x => x.ToString(CultureInfo.InvariantCulture)),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    Retry = new RetryPolicy(1, TimeSpan.Zero),
                    OnPermanentFailure = FailureAction.FaultPipeline,
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
        outputs.Should().HaveCount(2);
        outputs.Should().OnlyContain(o => !o.Result.IsSuccess);
    }

    [Fact]
    public async Task TypedCircuitBreaker_Rejection_ShouldPreserveTraceAndCorrelationFields()
    {
        var observer = new RecordingObserver();
        var rejectedEnvelope = new ProcessingEnvelope<int>
        {
            PipelineId = "source-pipeline",
            RunId = "source-run",
            TraceId = 99,
            Payload = 2,
            Metadata = MetadataBag.Empty,
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(
                new ProcessingEnvelope<int>
                {
                    PipelineId = "source-pipeline",
                    RunId = "source-run",
                    TraceId = 42,
                    Payload = 1,
                    Metadata = MetadataBag.Empty,
                    Lineage = [],
                    Attempt = 0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                },
                rejectedEnvelope))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        var rejected = observer.Events.OfType<CircuitBreakerRejectedEvent>().Single();
        rejected.TraceId.Should().Be(99);
        rejected.Attempt.Should().Be(0);
        rejected.PipelineId.Should().NotBeNullOrWhiteSpace();
        rejected.RunId.Should().NotBeNullOrWhiteSpace();
        rejected.StageId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TypedCircuitBreaker_Rejection_WithTransientDeadLetterPolicy_ShouldDeadLetter()
    {
        await using var stream = new MemoryStream();
        var serializer = new JsonLinesDeadLetterSerializer<int>();
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    Retry = new RetryPolicy(1, TimeSpan.Zero),
                    OnPermanentFailure = FailureAction.Skip,
                    OnRetryExhausted = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(stream, serializer)
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(2);
        outputs.Should().OnlyContain(o => !o.Result.IsSuccess);
        observer.Events.OfType<DeadLetterWrittenEvent>().Should().HaveCount(2);

        stream.Position = 0;
        var deadLetters = new List<DeadLetterEnvelope<int>>();
        await foreach (var envelope in serializer.ReadAsync(stream))
            deadLetters.Add(envelope);

        deadLetters.Should().HaveCount(2);
        deadLetters.Select(x => x.OriginalPayload).Should().Equal(1, 2);
        deadLetters.Should().Contain(x =>
            x.OriginalPayload == 1
            && x.Error.Type == ErrorType.Permanent
            && x.Error.Category == "TestFailure");
        deadLetters.Should().Contain(x =>
            x.OriginalPayload == 2
            && x.Error.Type == ErrorType.Transient
            && x.Error.Category == "CircuitBreaker");
    }

    [Fact]
    public async Task TypedCircuitBreaker_Rejection_WithTransientEmitFailurePolicy_ShouldEmitFailure()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    Retry = new RetryPolicy(1, TimeSpan.Zero),
                    OnPermanentFailure = FailureAction.Skip,
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
        outputs.Should().HaveCount(2);
        outputs.Should().Contain(o =>
            !o.Result.IsSuccess
            && o.Result.Error!.Value.Type == ErrorType.Transient
            && o.Result.Error.Value.Category == "CircuitBreaker");
    }

    [Fact]
    public async Task TypedCircuitBreaker_Rejection_WithRetryPolicy_ShouldNotExceedMaxAttempts()
    {
        var observer = new RecordingObserver();
        var transformer = new FailingStageTransformer<int, string>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    Retry = new RetryPolicy(2, TimeSpan.Zero),
                    OnPermanentFailure = FailureAction.Skip,
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().HaveCount(2);
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
        // Breaker opens on 3rd failure. Item 1 is terminal and skipped.
        // Item 2: rejected once by open breaker. Open-breaker rejection is terminal
        // and does not schedule additional retry attempts.
        outputs.Should().BeEmpty();
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
        observer.Events.OfType<RetryScheduledEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task PipelineBuilder_ModernApi_CircuitBreaker_ShouldNotRetryRejectedOpenBreakerWithinBudget()
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

        // Item 1 fails (1 failure), retry fails (2 failures -> breaker opens on 2nd failure).
        // Items 2-4 are rejected as transient terminal failures and are not retried.
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().HaveCount(3);
        observer.Events.OfType<RetryScheduledEvent>().Should().ContainSingle();
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

    [Fact]
    public async Task CircuitBreaker_Open_DeadLetter_ShouldWriteDeadLetterAndTerminalFailureOutput()
    {
        await using var stream = new MemoryStream();
        var serializer = new JsonLinesDeadLetterSerializer<int>();
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 2,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.DeadLetter,
                    OnRetryExhausted = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(stream, serializer)
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        // All 3 items should produce terminal failure output.
        // Items 1 and 2 fail the stage normally → DeadLetter + terminal failure.
        // Item 3 is rejected by the open breaker → must also produce DeadLetter + terminal failure.
        outputs.Should().HaveCount(3);
        outputs.Should().OnlyContain(o => !o.Result.IsSuccess);

        // Dead-letter should be written for all items, including breaker-rejected ones.
        observer.Events.OfType<DeadLetterWrittenEvent>().Should().HaveCount(3);

        // Circuit breaker rejection events confirm item 3 was rejected.
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task DeadLetter_ShouldBehaveConsistently_ForStageFailureAndBreakerRejection()
    {
        // Pipeline A: stage failure DeadLetter without circuit breaker.
        await using var streamA = new MemoryStream();
        var serializerA = new JsonLinesDeadLetterSerializer<int>();
        var observerA = new RecordingObserver();

        var runA = PipelineBuilder
            .From(new EnvelopeSource<int>(42))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(streamA, serializerA)
            )
            .WithObserver(observerA)
            .Run();

        var outputsA = await ReadOutputsAsync(runA.Outputs);
        await runA.Completion;

        // Pipeline B: breaker-triggered DeadLetter (breaker opens after 1 failure).
        await using var streamB = new MemoryStream();
        var serializerB = new JsonLinesDeadLetterSerializer<int>();
        var observerB = new RecordingObserver();

        var runB = PipelineBuilder
            .From(new EnvelopeSource<int>(42, 99))
            .Transform(
                new FailingStageTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.DeadLetter,
                    OnRetryExhausted = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(streamB, serializerB)
            )
            .WithObserver(observerB)
            .Run();

        var outputsB = await ReadOutputsAsync(runB.Outputs);
        await runB.Completion;

        // Both pipelines must produce terminal failure output for every item.
        outputsA.Single().Result.IsSuccess.Should().BeFalse();
        outputsB.Should().HaveCount(2);
        outputsB.Should().OnlyContain(o => !o.Result.IsSuccess);

        // Both pipelines must write dead-letter for every item.
        observerA.Events.OfType<DeadLetterWrittenEvent>().Should().ContainSingle();
        observerB.Events.OfType<DeadLetterWrittenEvent>().Should().HaveCount(2);

        // Pipeline B must have one breaker rejection (for item 2).
        observerB.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();

        // Dead-letter envelope contents must be consistent.
        streamA.Position = 0;
        streamB.Position = 0;
        var dlqA = new List<DeadLetterEnvelope<int>>();
        var dlqB = new List<DeadLetterEnvelope<int>>();
        await foreach (var e in serializerA.ReadAsync(streamA))
            dlqA.Add(e);
        await foreach (var e in serializerB.ReadAsync(streamB))
            dlqB.Add(e);

        dlqA.Single().OriginalPayload.Should().Be(42);
        dlqA.Single().Error.Category.Should().Be("TestFailure");

        dlqB.Should().HaveCount(2);
        dlqB[0].OriginalPayload.Should().Be(42);
        dlqB[0].Error.Category.Should().Be("TestFailure");
        dlqB[1].OriginalPayload.Should().Be(99);
        dlqB[1].Error.Category.Should().Be("CircuitBreaker");
    }

    #endregion
}
