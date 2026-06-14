using System.Globalization;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class RuntimeOptionsPassTests
{
    [Fact]
    public void ProcessingEnvelope_Create_ShouldPopulateRequiredDefaults()
    {
        var before = DateTimeOffset.UtcNow;

        var envelope = ProcessingEnvelope<int>.Create(42);

        envelope.Payload.Should().Be(42);
        envelope.PipelineId.Should().Be("default");
        envelope.RunId.Should().NotBeNullOrWhiteSpace();
        envelope.TraceId.Should().NotBe(0);
        envelope.Metadata.Should().BeSameAs(MetadataBag.Empty);
        envelope.Lineage.Should().BeEmpty();
        envelope.Attempt.Should().Be(0);
        envelope.CreatedAtUtc.Should().BeOnOrAfter(before);
        envelope.CreatedAtUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void ProcessingEnvelope_Create_WithExplicitValues_ShouldPreserveValues()
    {
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var metadata = MetadataBag.Empty.Set("tenant", "alpha");

        var envelope = ProcessingEnvelope<int>.Create(
            7,
            "orders-sync",
            "run-1",
            123,
            metadata,
            timestamp
        );

        envelope.Payload.Should().Be(7);
        envelope.PipelineId.Should().Be("orders-sync");
        envelope.RunId.Should().Be("run-1");
        envelope.TraceId.Should().Be(123);
        envelope.Metadata.Should().BeSameAs(metadata);
        envelope.Lineage.Should().BeEmpty();
        envelope.Attempt.Should().Be(0);
        envelope.CreatedAtUtc.Should().Be(timestamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProcessingEnvelope_Create_ShouldRejectInvalidPipelineId(string? pipelineId)
    {
        var act = () => ProcessingEnvelope<int>.Create(1, pipelineId!, "run", 1);

        act.Should().Throw<ArgumentException>().WithParameterName("pipelineId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProcessingEnvelope_Create_ShouldRejectInvalidRunId(string? runId)
    {
        var act = () => ProcessingEnvelope<int>.Create(1, "pipeline", runId!, 1);

        act.Should().Throw<ArgumentException>().WithParameterName("runId");
    }

    [Fact]
    public void ProcessingEnvelope_Create_WithExplicitTimestamp_ShouldUseTimestamp()
    {
        var now = new DateTimeOffset(2026, 6, 3, 8, 15, 0, TimeSpan.Zero);

        var envelope = ProcessingEnvelope<int>.Create(42, "typed-pipeline", "typed-run", 123, createdAtUtc: now);

        envelope.Payload.Should().Be(42);
        envelope.TraceId.Should().Be(123);
        envelope.PipelineId.Should().Be("typed-pipeline");
        envelope.RunId.Should().Be("typed-run");
        envelope.CreatedAtUtc.Should().Be(now);
    }

    [Fact]
    public async Task PipelineBuilder_WithoutPipelineId_ShouldKeepExistingEnvelopePipelineId()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Envelope!.PipelineId.Should().Be("source-pipeline");
    }

    [Fact]
    public async Task PipelineBuilder_WithPipelineId_ShouldUseConfiguredIdInOutputEnvelope()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithPipelineId("orders-sync")
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Envelope!.PipelineId.Should().Be("orders-sync");
    }

    [Fact]
    public async Task PipelineBuilder_WithPipelineId_ShouldUseConfiguredIdInEvents()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithPipelineId("billing.import.v1")
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.Should().NotBeEmpty();
        observer.Events.Should().OnlyContain(e => e.PipelineId == "billing.import.v1");
    }

    [Fact]
    public async Task PipelineBuilder_WithPipelineId_ShouldNotChangeRunIdGeneration()
    {
        var sourceEnvelope = ProcessingEnvelope<int>.Create(
            1,
            "source-pipeline",
            "source-run",
            123
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(sourceEnvelope))
            .WithPipelineId("configured-pipeline")
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Envelope!.PipelineId.Should().Be("configured-pipeline");
        outputs.Single().Envelope!.RunId.Should().Be("source-run");
    }

    [Fact]
    public async Task PipelineBuilder_WithPipelineId_ShouldNotChangeTraceIdGeneration()
    {
        var sourceEnvelope = ProcessingEnvelope<int>.Create(
            1,
            "source-pipeline",
            "source-run",
            987
        );

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(sourceEnvelope))
            .WithPipelineId("configured-pipeline")
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Envelope!.TraceId.Should().Be(987);
    }

    [Fact]
    public async Task PipelineBuilder_WithPipelineId_ShouldUseConfiguredIdInDeadLetterRecord()
    {
        await using var stream = new MemoryStream();
        var serializer = new JsonLinesDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(42))
            .WithPipelineId("dead-letter-pipeline")
            .Transform(
                new FailingTransformer<int, string>(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream, serializer)
            )
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        stream.Position = 0;
        var deadLetters = new List<DeadLetterEnvelope<int>>();
        await foreach (var envelope in serializer.ReadAsync(stream))
            deadLetters.Add(envelope);

        deadLetters.Single().PipelineId.Should().Be("dead-letter-pipeline");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void PipelineBuilder_WithPipelineId_ShouldRejectInvalidValues(string? pipelineId)
    {
        var act = () => PipelineBuilder.From(new EnvelopeSource<int>(1)).WithPipelineId(pipelineId!);

        act.Should().Throw<ArgumentException>().WithParameterName("pipelineId");
    }

    [Fact]
    public void PipelineRuntimeOptions_Defaults_AreTypedOnlySafe()
    {
        var options = new PipelineRuntimeOptions();

        options.MaxConcurrency.Should().Be(1);
        options.InputCapacity.Should().Be(1024);
        options.InputFullMode.Should().Be(BoundedChannelFullMode.Wait);
        options.OutputPolicy.Should().Be(PipelineOutputPolicy.EmitAll);
        options.OrderingMode.Should().Be(PipelineOrderingMode.Unordered);
        options.ObserverDispatch.Should().BeSameAs(ObserverDispatchOptions.Inline);
        options.Clock.Should().BeSameAs(SystemPipelineClock.Instance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PipelineRuntimeOptions_InvalidMaxConcurrency_Throws(int maxConcurrency)
    {
        var options = new PipelineRuntimeOptions { MaxConcurrency = maxConcurrency };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MaxConcurrency");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PipelineRuntimeOptions_InvalidInputCapacity_Throws(int inputCapacity)
    {
        var options = new PipelineRuntimeOptions { InputCapacity = inputCapacity };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("InputCapacity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PipelineRuntimeOptions_InvalidOutputCapacity_Throws(int outputCapacity)
    {
        var options = new PipelineRuntimeOptions { OutputCapacity = outputCapacity };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("OutputCapacity");
    }

    [Fact]
    public void PipelineRuntimeOptions_PreserveOrderWithParallelism_ThrowsUntilImplemented()
    {
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 2,
            OrderingMode = PipelineOrderingMode.PreserveInputOrder,
        };

        var act = () => options.Validate();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*PreserveInputOrder*MaxConcurrency > 1*");
    }

    [Fact]
    public void PipelineRuntimeOptions_MaxConcurrency_ShouldBeEffectiveTypedConcurrencyName()
    {
        var options = new PipelineRuntimeOptions { MaxConcurrency = 3 };

        options.Validate();

        options.EffectiveMaxConcurrency.Should().Be(3);
    }

    [Fact]
    public void PipelineRuntimeOptions_ConflictingConcurrencyNames_ShouldThrow()
    {
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 2,
            MaxDegreeOfParallelism = 3,
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxConcurrency*MaxDegreeOfParallelism*");
    }

    [Fact]
    public async Task RuntimeOptions_DefaultWithSinkAndEmitAll_ShouldRequireOutputConsumer()
    {
        const int defaultCapacity = 1024;
        var sink = new CountingEnvelopeSink<string>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, defaultCapacity + 1).ToArray()))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .To(sink);

        await sink.WaitForCountAsync(defaultCapacity, TimeSpan.FromSeconds(5));
        Func<Task> extraWrite = async () =>
            await sink.WaitForCountAsync(defaultCapacity + 1, TimeSpan.FromMilliseconds(150));

        await extraWrite.Should().ThrowAsync<TimeoutException>(
            "default typed output must be bounded even when a sink is attached");

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Select(o => o.Result.Value)
            .Should()
            .Equal(Enumerable.Range(1, defaultCapacity + 1).Select(x => x.ToString(CultureInfo.InvariantCulture)));
        sink.Payloads.Should()
            .Equal(Enumerable.Range(1, defaultCapacity + 1).Select(x => x.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task RuntimeOptions_DefaultWithSinkAndSuppressSuccess_ShouldCompleteWithoutOutputConsumer()
    {
        const int defaultCapacity = 1024;
        var sink = new EnvelopeCollectingSink<string>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, defaultCapacity + 1).ToArray()))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(sink);

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs);

        outputs.Should().BeEmpty();
        sink.Payloads.Should()
            .Equal(Enumerable.Range(1, defaultCapacity + 1).Select(x => x.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task RuntimeOptions_OutputCapacity_ShouldCreateBoundedOutputWhenConfigured()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    OutputCapacity = 1,
                    OutputFullMode = BoundedChannelFullMode.Wait,
                }
            )
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(o => o.Result.Value).Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task RuntimeOptions_BoundedOutput_WithSinkAndUnreadOutputs_ShouldRequireOutputConsumer()
    {
        var sink = new CountingEnvelopeSink<string>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    OutputCapacity = 1,
                    OutputFullMode = BoundedChannelFullMode.Wait,
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .To(sink);

        await sink.WaitForCountAsync(1, TimeSpan.FromSeconds(5));

        run.Completion.IsCompleted.Should().BeFalse(
            "bounded output with Wait backpressures the run when outputs are not consumed"
        );
        sink.Payloads.Should().Equal("1");

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Select(o => o.Result.Value).Should().Equal("1", "2");
        sink.Payloads.Should().Equal("1", "2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RuntimeOptions_OutputCapacity_ShouldRejectInvalidCapacity(int capacity)
    {
        var act = () =>
            PipelineBuilder
                .From(new EnvelopeSource<int>(1))
                .WithRuntimeOptions(new PipelineRuntimeOptions { OutputCapacity = capacity });

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("OutputCapacity");
    }

    [Fact]
    public void RuntimeOptions_OutputFullMode_ShouldRejectUndefinedValue()
    {
        var act = () =>
            PipelineBuilder
                .From(new EnvelopeSource<int>(1))
                .WithRuntimeOptions(
                    new PipelineRuntimeOptions
                    {
                        OutputCapacity = 1,
                        OutputFullMode = (BoundedChannelFullMode)999,
                    }
                );

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("OutputFullMode");
    }

    [Fact]
    public async Task RuntimeOptions_CustomClock_ShouldBeUsedByTypedRuntimeEvents()
    {
        var now = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualPipelineClock(now);
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.Should().OnlyContain(e => e.TimestampUtc == now);
    }

    [Fact]
    public async Task RuntimeOptions_CustomClock_ShouldControlEnvelopeCreatedAtUtc_WhenSourceLeavesDefault()
    {
        var now = new DateTimeOffset(2026, 6, 2, 11, 0, 0, TimeSpan.Zero);
        var clock = new ManualPipelineClock(now);
        var sourceEnvelope = new ProcessingEnvelope<int>
        {
            PipelineId = "source-pipeline",
            RunId = "source-run",
            TraceId = 44,
            Payload = 1,
            Metadata = MetadataBag.Empty,
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = default,
        };

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(sourceEnvelope))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Envelope!.CreatedAtUtc.Should().Be(now);
    }

    [Fact]
    public async Task PipelineClock_Custom_ShouldControlStageTimeoutBudget()
    {
        var clock = new ManualPipelineClock(new DateTimeOffset(2026, 6, 2, 12, 30, 0, TimeSpan.Zero));
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(
                new AdvancingFailingTransformer<int, string>(
                    clock,
                    TimeSpan.FromMilliseconds(20),
                    ErrorType.Transient
                ),
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(
                        maxRetries: 2,
                        delay: TimeSpan.Zero,
                        strategy: BackoffStrategy.Fixed
                    ),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(10) },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.IsSuccess.Should().BeFalse();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task PipelineClock_Custom_ShouldControlRetryDelayScheduling()
    {
        var clock = new ManualPipelineClock(new DateTimeOffset(2026, 6, 2, 12, 45, 0, TimeSpan.Zero));
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(
                new AdvancingFailingTransformer<int, string>(
                    clock,
                    TimeSpan.FromMilliseconds(2),
                    ErrorType.Transient
                ),
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(
                        maxRetries: 2,
                        delay: TimeSpan.FromMilliseconds(10),
                        strategy: BackoffStrategy.Fixed
                    ),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(10) },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.IsSuccess.Should().BeFalse();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ObserverDispatch_Default_ShouldRemainInline()
    {
        var observer = new ThreadRecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.ThreadIds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ObserverDispatch_BufferedReliable_ShouldFlushBeforeCompletion()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 16,
                        FullMode = BoundedChannelFullMode.Wait,
                        FlushOnCompletion = true,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
        observer.Events.OfType<StageSucceededEvent>().Should().HaveCount(3);
    }

    [Fact]
    public async Task ObserverDispatch_BufferedReliable_ShouldApplyBackpressureWhenQueueFull()
    {
        var observer = new SlowObserver(TimeSpan.FromMilliseconds(5));

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, 12).ToArray()))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 1,
                        FullMode = BoundedChannelFullMode.Wait,
                        FlushOnCompletion = true,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().HaveCount(12);
        observer.EventsSeen.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ObserverDispatch_BufferedModes_ShouldCompleteDispatcherExactlyOnce()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                        FlushOnCompletion = true,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        await run.DisposeAsync();
        await run.DisposeAsync();

        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ObserverDispatch_BufferedModes_ShouldNotDispatchAfterCompletion()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                        FlushOnCompletion = true,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;
        var countAfterCompletion = observer.Events.Count;

        await run.DisposeAsync();
        await Task.Delay(25);

        observer.Events.Should().HaveCount(countAfterCompletion);
    }

    [Fact]
    public async Task ObserverDispatch_BufferedBestEffort_ShouldNotBlockPipelineOnSlowObserver()
    {
        var observer = new SlowObserver(TimeSpan.FromMilliseconds(50));

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, 30).ToArray()))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedBestEffort,
                        Capacity = 1,
                        FullMode = BoundedChannelFullMode.DropWrite,
                        FlushOnCompletion = false,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observer.EventsSeen.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ObserverDispatch_BufferedBestEffort_ShouldNotFaultPipelineOnThrowingObserver()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedBestEffort,
                        Capacity = 8,
                        FailureMode = ObserverFailureMode.Ignore,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new ThrowingObserver())
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(3);
    }

    [Fact]
    public async Task ObserverDispatch_BufferedReliable_ShouldRespectFailureMode()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                        FailureMode = ObserverFailureMode.FaultPipeline,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new ThrowingObserver(), failurePolicy: ObserverFailurePolicy.Ignore)
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ObserverDispatch_BufferedReliable_ShouldRespectRegistrationFaultPolicy()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                        FailureMode = ObserverFailureMode.UseRegistrationPolicy,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new ThrowingObserver(), failurePolicy: ObserverFailurePolicy.FaultPipeline)
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(ObserverFailureMatrixCases))]
    public async Task ObserverDispatch_FailureMatrix_ShouldUseFullPipelineCompletion(
        ObserverDispatchMode mode,
        ObserverFailureMode failureMode,
        ObserverReliability reliability,
        ObserverFailurePolicy failurePolicy,
        bool shouldFault)
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = mode,
                        Capacity = 8,
                        FailureMode = failureMode,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new ThrowingObserver(), reliability, failurePolicy)
            .Run();

        var act = async () => await run.Completion;

        if (shouldFault)
            await act.Should().ThrowAsync<InvalidOperationException>();
        else
        {
            _ = await ReadOutputsAsync(run.Outputs);
            await act.Should().NotThrowAsync();
        }
    }

    public static TheoryData<ObserverDispatchMode, ObserverFailureMode, ObserverReliability, ObserverFailurePolicy, bool>
        ObserverFailureMatrixCases()
    {
        return new TheoryData<ObserverDispatchMode, ObserverFailureMode, ObserverReliability, ObserverFailurePolicy, bool>
        {
            { ObserverDispatchMode.Inline, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.Critical, ObserverFailurePolicy.Ignore, true },
            { ObserverDispatchMode.Inline, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.Reliable, ObserverFailurePolicy.FaultPipeline, true },
            { ObserverDispatchMode.Inline, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.Reliable, ObserverFailurePolicy.Ignore, false },
            { ObserverDispatchMode.BufferedReliable, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.Critical, ObserverFailurePolicy.Ignore, true },
            { ObserverDispatchMode.BufferedReliable, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.Reliable, ObserverFailurePolicy.FaultPipeline, true },
            { ObserverDispatchMode.BufferedReliable, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.Reliable, ObserverFailurePolicy.Ignore, false },
            { ObserverDispatchMode.BufferedReliable, ObserverFailureMode.Ignore, ObserverReliability.Critical, ObserverFailurePolicy.Ignore, false },
            { ObserverDispatchMode.BufferedReliable, ObserverFailureMode.Ignore, ObserverReliability.Reliable, ObserverFailurePolicy.FaultPipeline, false },
            { ObserverDispatchMode.BufferedBestEffort, ObserverFailureMode.UseRegistrationPolicy, ObserverReliability.BestEffort, ObserverFailurePolicy.Log, false },
            { ObserverDispatchMode.BufferedBestEffort, ObserverFailureMode.FaultPipeline, ObserverReliability.BestEffort, ObserverFailurePolicy.Ignore, true },
        };
    }

    [Fact]
    public async Task RemoveObserver_Inline_RemovesFailingObserver_AndKeepsHealthyObserver()
    {
        var failing = new CountingThrowingObserver();
        var healthy = new RecordingObserver();
        var source = new GateControlledSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .WithRuntimeOptions(new PipelineRuntimeOptions { ObserverDispatch = ObserverDispatchOptions.Inline })
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(failing, failurePolicy: ObserverFailurePolicy.RemoveObserver)
            .WithObserver(healthy)
            .Run();

        await failing.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseRemainingItems();
        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        failing.CallCount.Should().Be(1);
        healthy.Events.OfType<StageStartedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveObserver_Buffered_RemovesFailingObserver_AndKeepsHealthyObserver()
    {
        var failing = new CountingThrowingObserver();
        var healthy = new RecordingObserver();
        var source = new GateControlledSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(failing, failurePolicy: ObserverFailurePolicy.RemoveObserver)
            .WithObserver(healthy)
            .Run();

        await failing.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseRemainingItems();
        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        failing.CallCount.Should().Be(1);
        healthy.Events.OfType<StageStartedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveObserver_DoesNotRemoveObserver_WhenPolicyIsIgnore()
    {
        var failing = new CountingThrowingObserver();
        var source = new GateControlledSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .WithRuntimeOptions(new PipelineRuntimeOptions { ObserverDispatch = ObserverDispatchOptions.Inline })
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(failing, failurePolicy: ObserverFailurePolicy.Ignore)
            .Run();

        await failing.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseRemainingItems();
        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        failing.CallCount.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData(ObserverReliability.Critical, ObserverFailurePolicy.RemoveObserver)]
    [InlineData(ObserverReliability.BestEffort, ObserverFailurePolicy.FaultPipeline)]
    public async Task RemoveObserver_DoesNotOverrideFaultPriority(
        ObserverReliability reliability,
        ObserverFailurePolicy failurePolicy)
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new CountingThrowingObserver(), reliability, failurePolicy)
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RemoveObserver_DoesNotOverrideGlobalFaultPipeline()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                        FailureMode = ObserverFailureMode.FaultPipeline,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new CountingThrowingObserver(), failurePolicy: ObserverFailurePolicy.RemoveObserver)
            .Run();

        var act = async () => await run.Completion;

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RemoveObserver_IsIdempotent_WhenObserverFailsMultipleTimes()
    {
        var failing = new CountingThrowingObserver();
        var source = new GateControlledSource<int>(1, 2, 3);

        var run = PipelineBuilder
            .From(source)
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 8,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(failing, failurePolicy: ObserverFailurePolicy.RemoveObserver)
            .Run();

        await failing.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseRemainingItems();
        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        failing.CallCount.Should().Be(1);
    }

    [Fact]
    public void ObserverDispatch_BufferedReliable_ShouldRejectFlushOnCompletionFalse()
    {
        var act = () =>
            PipelineBuilder
                .From(new EnvelopeSource<int>(1))
                .WithRuntimeOptions(
                    new PipelineRuntimeOptions
                    {
                        ObserverDispatch = new ObserverDispatchOptions
                        {
                            Mode = ObserverDispatchMode.BufferedReliable,
                            FlushOnCompletion = false,
                        },
                    }
                );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("BufferedReliable requires FlushOnCompletion = true.");
    }

    [Fact]
    public async Task ObserverDispatch_ObserverFailureEvent_ShouldUsePipelineClock()
    {
        var now = new DateTimeOffset(2026, 6, 3, 9, 30, 0, TimeSpan.Zero);
        var clock = new ManualPipelineClock(now);
        var recording = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(new ThrowingObserver())
            .WithObserver(recording)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        var failureEvents = recording.Events.OfType<ObserverFailedEvent>().ToArray();
        failureEvents.Should().NotBeEmpty();
        failureEvents.Should().OnlyContain(e => e.TimestampUtc == now);
    }

    [Fact]
    public async Task RuntimeOptions_Stress_BufferedObservers_NoHang_NoUnobservedExceptions()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, 100).ToArray()))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    ObserverDispatch = new ObserverDispatchOptions
                    {
                        Mode = ObserverDispatchMode.BufferedReliable,
                        Capacity = 128,
                        FullMode = BoundedChannelFullMode.Wait,
                        FlushOnCompletion = true,
                    },
                }
            )
            .Transform(new EnvelopeTransformer<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().HaveCount(100);
        observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task RuntimeOptions_Stress_BoundedOutput_WithConsumer_NoLostItems()
    {
        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, 100).ToArray()))
            .WithRuntimeOptions(
                new PipelineRuntimeOptions
                {
                    OutputCapacity = 4,
                    OutputFullMode = BoundedChannelFullMode.Wait,
                }
            )
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Select(o => o.Result.Value).Should().Equal(Enumerable.Range(1, 100));
    }

    [Fact]
    public async Task TypedPipeline_DrainAsync_ShouldTimeoutWhenSourceIsBlockedInsideMoveNextAsync()
    {
        var source = new GateControlledSource<int>(1, 2);

        var run = PipelineBuilder
            .From(source)
            .Transform(new EnvelopeTransformer<int, int>(x => x))
            .Run();

        await source.FirstItemYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = async () => await run.DrainAsync(TimeSpan.FromMilliseconds(50));
        await act.Should().ThrowAsync<TimeoutException>();

        source.ReleaseRemainingItems();
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Select(o => o.Result.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task CircuitBreaker_CompatibilityThreshold_ShouldPreserveDefaultBehavior()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .Transform(
                new FailingTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.CompatibilityThreshold,
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

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task CircuitBreaker_RatioMode_ShouldNotOpenBeforeMinimumThroughput()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .Transform(
                new FailingTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
                        FailureRatio = 0.5,
                        MinimumThroughput = 3,
                        SamplingDuration = TimeSpan.FromMinutes(1),
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreaker_RatioMode_ShouldOpenWhenFailureRatioReached()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3, 4))
            .Transform(
                new FailingTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
                        FailureRatio = 0.5,
                        MinimumThroughput = 2,
                        SamplingDuration = TimeSpan.FromMinutes(1),
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task PipelineClock_Custom_ShouldControlCircuitBreakerSamplingWindow()
    {
        var observer = new RecordingObserver();
        var clock = new ManualPipelineClock(new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero));

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(
                new AdvancingFailingTransformer<int, string>(clock, TimeSpan.FromSeconds(2)),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
                        FailureRatio = 0.5,
                        MinimumThroughput = 2,
                        SamplingDuration = TimeSpan.FromSeconds(1),
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreaker_RatioMode_ShouldNotManageRetry()
    {
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1))
            .Transform(
                new FailingTransformer<int, string>(ErrorType.Transient),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
                        FailureRatio = 1,
                        MinimumThroughput = 10,
                        SamplingDuration = TimeSpan.FromMinutes(1),
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Result.IsSuccess.Should().BeFalse();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryAttemptedEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreaker_FailureRatio_ShouldUseSamplingModeOnlyWhenConfigured()
    {
        var observer = new RecordingObserver();
        var transformer = new CountingFailingTransformer<int, string>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
                        FailureRatio = 1,
                        MinimumThroughput = 1,
                        SamplingDuration = TimeSpan.FromMinutes(1),
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        transformer.CallCount.Should().Be(1);
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task PipelineClock_Stress_TimeoutRetryBudget_Deterministic()
    {
        var clock = new ManualPipelineClock(new DateTimeOffset(2026, 6, 2, 13, 0, 0, TimeSpan.Zero));
        var observer = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(Enumerable.Range(1, 10).ToArray()))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(
                new AdvancingFailingTransformer<int, string>(
                    clock,
                    TimeSpan.FromMilliseconds(10),
                    ErrorType.Transient
                ),
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(
                        maxRetries: 2,
                        delay: TimeSpan.FromMilliseconds(5),
                        strategy: BackoffStrategy.Fixed
                    ),
                    Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromMilliseconds(12) },
                    OnRetryExhausted = FailureAction.Skip,
                }
            )
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().BeEmpty();
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().HaveCount(10);
    }

    [Fact]
    public async Task TypedRuntime_CircuitBreaker_RatioMode_DeadLetter_ShouldStillEmitTerminalOutput()
    {
        await using var stream = new MemoryStream();
        var serializer = new JsonLinesDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnvelopeSource<int>(1, 2, 3))
            .Transform(
                new FailingTransformer<int, string>(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
                        FailureRatio = 0.5,
                        MinimumThroughput = 2,
                        SamplingDuration = TimeSpan.FromMinutes(1),
                        BreakDuration = TimeSpan.FromMinutes(5),
                    },
                    OnPermanentFailure = FailureAction.DeadLetter,
                    OnRetryExhausted = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(stream, serializer)
            )
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().HaveCount(3);
        outputs.Should().OnlyContain(o => !o.Result.IsSuccess);
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
                .Select(payload =>
                    ProcessingEnvelope<T>.Create(
                        payload,
                        "source-pipeline",
                        "source-run",
                        (ulong)Random.Shared.Next(1, int.MaxValue)
                    )
                )
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

    private sealed class GateControlledSource<T> : IPipelineSource<T>
    {
        private readonly ProcessingEnvelope<T>[] _items;
        private readonly TaskCompletionSource _releaseRemainingItems =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GateControlledSource(params T[] payloads)
        {
            _items = payloads
                .Select(payload =>
                    ProcessingEnvelope<T>.Create(
                        payload,
                        "source-pipeline",
                        "source-run",
                        (ulong)Random.Shared.Next(1, int.MaxValue)
                    )
                )
                .ToArray();
        }

        public TaskCompletionSource FirstItemYielded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            if (_items.Length == 0)
                yield break;

            yield return _items[0];
            FirstItemYielded.TrySetResult();

            await _releaseRemainingItems.Task.WaitAsync(ct).ConfigureAwait(false);

            for (var i = 1; i < _items.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return _items[i];
            }
        }

        public void ReleaseRemainingItems() => _releaseRemainingItems.TrySetResult();

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

    private sealed class FailingTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly ErrorType _errorType;

        public FailingTransformer(ErrorType errorType = ErrorType.Permanent)
        {
            _errorType = errorType;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            return ValueTask.FromResult(
                StageResult<TOutput>.Failure(
                    new SmartPipeError("boom", _errorType, "TestFailure")
                )
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdvancingFailingTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        private readonly ManualPipelineClock _clock;
        private readonly TimeSpan _advanceBy;
        private readonly ErrorType _errorType;

        public AdvancingFailingTransformer(
            ManualPipelineClock clock,
            TimeSpan advanceBy,
            ErrorType errorType = ErrorType.Permanent
        )
        {
            _clock = clock;
            _advanceBy = advanceBy;
            _errorType = errorType;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            _clock.Advance(_advanceBy);
            return ValueTask.FromResult(
                StageResult<TOutput>.Failure(
                    new SmartPipeError("boom", _errorType, "TestFailure")
                )
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingFailingTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>
    {
        public int CallCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default
        )
        {
            CallCount++;
            return ValueTask.FromResult(
                StageResult<TOutput>.Failure(
                    new SmartPipeError("boom", ErrorType.Permanent, "TestFailure")
                )
            );
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

    private sealed class CountingEnvelopeSink<T> : IPipelineSink<T>
    {
        private readonly List<T> _payloads = [];
        private readonly object _gate = new();
        private TaskCompletionSource _countChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<T> Payloads
        {
            get
            {
                lock (_gate)
                    return _payloads.ToArray();
            }
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _payloads.Add(envelope.Payload);
                _countChanged.TrySetResult();
                _countChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int expectedCount, TimeSpan timeout)
        {
            while (true)
            {
                Task waitTask;
                lock (_gate)
                {
                    if (_payloads.Count >= expectedCount)
                        return;

                    waitTask = _countChanged.Task;
                }

                await waitTask.WaitAsync(timeout).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        private readonly List<PipelineEvent> _events = [];

        public IReadOnlyList<PipelineEvent> Events => _events;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            lock (_events)
                _events.Add(pipelineEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThreadRecordingObserver : IPipelineObserver
    {
        private readonly List<int> _threadIds = [];

        public IReadOnlyList<int> ThreadIds => _threadIds;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _threadIds.Add(Environment.CurrentManagedThreadId);
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

    private sealed class CountingThrowingObserver : IPipelineObserver
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource FirstFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            FirstFailure.TrySetResult();
            throw new InvalidOperationException("observer boom");
        }
    }

    private sealed class SlowObserver : IPipelineObserver
    {
        private readonly TimeSpan _delay;

        public SlowObserver(TimeSpan delay)
        {
            _delay = delay;
        }

        public int EventsSeen { get; private set; }

        public async ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            EventsSeen++;
            await Task.Delay(_delay, ct);
        }
    }

    private sealed class ManualPipelineClock : IPipelineClock
    {
        private DateTimeOffset _now;

        public ManualPipelineClock(DateTimeOffset now)
        {
            _now = now;
        }

        public DateTimeOffset GetUtcNow() => _now;

        public long GetTimestamp() => _now.UtcTicks;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan value)
        {
            _now += value;
        }
    }
}
