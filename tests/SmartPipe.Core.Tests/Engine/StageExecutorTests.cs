#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class StageExecutorTests
{
    private static readonly TimeSpan MinimalRetryDelay = TimeSpan.FromTicks(1);

    [Fact]
    public async Task StageExecutor_Retry_RetriesConfiguredAttempts()
    {
        var transformer = new FailThenSucceedTransformer<int>(failuresBeforeSuccess: 2);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([42]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(2, MinimalRetryDelay),
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(42);
        transformer.Attempts.Should().Equal(0, 1, 2);
        observer.Events.OfType<RetryScheduledEvent>().Should().HaveCount(2);
        observer.Events.OfType<RetryExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task StageExecutor_Retry_StopsAfterMaxAttempts()
    {
        var transformer = new AlwaysFailingTransformer<int>(ErrorType.Transient);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([7]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(2, MinimalRetryDelay),
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Type.Should().Be(ErrorType.Transient);
        transformer.Attempts.Should().Equal(0, 1, 2);
        observer.Events.OfType<RetryScheduledEvent>().Should().HaveCount(2);
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle()
            .Which.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task StageExecutor_Timeout_ProducesTimeoutFailure()
    {
        var transformer = new BlockingTimeoutTransformer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                })
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Category.Should().Be("Timeout");
        transformer.CancellationObserved.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task StageExecutor_Timeout_CooperativeOnlyDoesNotRetryWhileAttemptIsLate()
    {
        var transformer = new ReleasableLateAttemptTransformer<int>();
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        CancellationGracePeriod = TimeSpan.FromMilliseconds(25),
                        LateAttemptFinalizationTimeout = TimeSpan.FromSeconds(5),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        await transformer.WaitForAttemptAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        var output = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        output.Result.IsSuccess.Should().BeFalse();
        output.Result.Error!.Value.Category.Should().Be("Timeout");
        transformer.AttemptCount.Should().Be(1);
        transformer.MaxConcurrent.Should().Be(1);
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();

        transformer.ReleaseLateAttempts();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task StageExecutor_Timeout_DetachAndRetryIdempotentAllowsExplicitOverlap()
    {
        var transformer = new ReleasableLateAttemptTransformer<int>(
            completeRetryAttemptsImmediately: true);

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        RetryMode = TimeoutRetryMode.DetachAndRetryIdempotent,
                        LateAttemptFinalizationTimeout = TimeSpan.FromSeconds(5),
                    },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .Run();

        await transformer.WaitForAttemptAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.WaitForAttemptAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

        transformer.MaxConcurrent.Should().Be(2);
        transformer.ReleaseLateAttempts();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(9);
        transformer.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task StageExecutor_Timeout_LateAttemptCompletesBeforeStageDisposal()
    {
        var transformer = new ReleasableLateAttemptTransformer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        RetryMode = TimeoutRetryMode.DetachWithoutRetry,
                        LateAttemptFinalizationTimeout = TimeSpan.FromSeconds(5),
                    },
                })
            .Run();

        await transformer.WaitForAttemptAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        var output = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        output.Result.Error!.Value.Category.Should().Be("Timeout");

        transformer.ReleaseLateAttempts();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        transformer.LateAttemptCompletedOrder.Should().BePositive();
        transformer.DisposeOrder.Should().BePositive();
        transformer.LateAttemptCompletedOrder.Should().BeLessThan(transformer.DisposeOrder);
    }

    [Fact]
    public async Task StageExecutor_Timeout_LateAttemptFinalizationTimeoutFaultsCompletion()
    {
        var transformer = new ReleasableLateAttemptTransformer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        RetryMode = TimeoutRetryMode.DetachWithoutRetry,
                        LateAttemptFinalizationTimeout = TimeSpan.FromMilliseconds(25),
                    },
                })
            .Run();

        await transformer.WaitForAttemptAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        var output = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        output.Result.Error!.Value.Category.Should().Be("Timeout");

        try
        {
            var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await completion.Should().ThrowAsync<TimeoutException>()
                .WithMessage("*Late stage attempt*");
            transformer.DisposeOrder.Should().Be(0);
        }
        finally
        {
            transformer.ReleaseLateAttempts();
        }
    }

    [Fact]
    public async Task StageExecutor_Timeout_MultipleDetachedAttempts_DisposeWaitsForDeferredStageCleanup()
    {
        var transformer = new ReleasableLateAttemptTransformer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        RetryMode = TimeoutRetryMode.DetachAndRetryIdempotent,
                        LateAttemptFinalizationTimeout = TimeSpan.FromMilliseconds(25),
                    },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .Run();

        await transformer.WaitForAttemptAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.WaitForAttemptAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        var output = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        output.Result.Error!.Value.Category.Should().Be("Timeout");

        await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<TimeoutException>()
            .WithMessage("*Late stage attempt*");
        transformer.DisposeOrder.Should().Be(0);

        transformer.ReleaseLateAttempts();
        await run.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        transformer.LateAttemptCompletedOrder.Should().BePositive();
        transformer.DisposeOrder.Should().BePositive();
        transformer.LateAttemptCompletedOrder.Should().BeLessThan(transformer.DisposeOrder);
    }

    [Fact]
    public async Task StageExecutor_Timeout_StageBudgetIncludesCancellationGrace()
    {
        var clock = new AdvancingPipelineClock(
            new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));
        var transformer = new GraceAdvancingTimeoutTransformer<int>(
            clock,
            TimeSpan.FromMilliseconds(45));
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        StageTimeout = TimeSpan.FromMilliseconds(50),
                        CancellationGracePeriod = TimeSpan.FromMilliseconds(100),
                    },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Category.Should().Be("Timeout");
        transformer.Attempts.Should().Be(1);
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task StageExecutor_Timeout_WhenStageThrowsTimeoutException_ShouldClassifyAsStageException()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                new ThrowingExceptionTransformer(new TimeoutException("stage timeout exception")),
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromSeconds(5),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Type.Should().Be(ErrorType.Permanent);
        outputs[0].Result.Error!.Value.Category.Should().Be("StageException");
    }

    [Fact]
    public async Task StageExecutor_Timeout_WhenAttemptSucceedsDuringGrace_ShouldReturnSuccessWithoutRetry()
    {
        var transformer = new ReleaseAfterCancellationTransformer<int>();
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        CancellationGracePeriod = TimeSpan.FromSeconds(5),
                    },
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transformer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.Release();
        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(9);
        transformer.Attempts.Should().Be(1);
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task StageExecutor_Timeout_CooperativeCancellation_ShouldRespectDetachWithoutRetry()
    {
        var transformer = new BlockingTimeoutTransformer<int>();
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                        RetryMode = TimeoutRetryMode.DetachWithoutRetry,
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Category.Should().Be("Timeout");
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task StageExecutor_CircuitBreaker_OpensAfterPolicy()
    {
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([1, 2]))
            .Transform(
                new AlwaysFailingTransformer<int>(ErrorType.Transient),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(1),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().HaveCount(2);
        outputs.Should().OnlyContain(output => !output.Result.IsSuccess);
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle()
            .Which.TraceId.Should().Be(2);
    }

    [Fact]
    public async Task CircuitBreakerClosed_EmitsClosedEvent()
    {
        var observer = new RecordingPipelineObserver();
        var clock = new AdvancingPipelineClock(
            new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));

        var run = PipelineBuilder
            .From(new ClockAdvancingSource<int>([1, 2, 3], clock, advanceAfterFirst: TimeSpan.FromSeconds(2)))
            .Transform(
                new FailFirstItemTransformer(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromSeconds(1),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                })
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                Clock = clock,
            })
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerClosedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreakerBreakDuration_UsesMonotonicRuntimeClock()
    {
        var observer = new RecordingPipelineObserver();
        var clock = new UtcJumpingPipelineClock(
            new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));

        var run = PipelineBuilder
            .From(new UtcJumpingSource<int>([1, 2], clock, jumpBeforeSecond: TimeSpan.FromSeconds(2)))
            .Transform(
                new FailFirstItemTransformer(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromSeconds(1),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                })
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                Clock = clock,
            })
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle()
            .Which.TraceId.Should().Be(2);
        observer.Events.OfType<CircuitBreakerClosedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreakerRejected_NotRetriedIntoOpenBreaker()
    {
        var transformer = new AlwaysFailingTransformer<int>(ErrorType.Transient);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([1, 2]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(1),
                    },
                    Retry = new RetryPolicy(5, MinimalRetryDelay),
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().HaveCount(2);
        transformer.Attempts.Should().Equal(0);
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle()
            .Which.TraceId.Should().Be(2);
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task StageExecutor_DeadLetter_WritesTerminalFailure()
    {
        await using var stream = new MemoryStream();
        var serializer = new CapturingDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([123]))
            .Transform(
                new AlwaysFailingTransformer<int>(ErrorType.Permanent),
                new StageFailureOptions
                {
                    OnPermanentFailure = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(stream, serializer))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        serializer.Written.Should().ContainSingle();
        var deadLetter = serializer.Written[0];
        deadLetter.OriginalPayload.Should().Be(123);
        deadLetter.Error.Type.Should().Be(ErrorType.Permanent);
        deadLetter.StageId.Should().Be("stage-1");
    }

    [Fact]
    public async Task StageExecutor_DeadLetterWriteFailure_DoesNotRecordSuccess()
    {
        await using var stream = new MemoryStream();
        var expected = new IOException("dead-letter persistence failed");
        var serializer = new ThrowingDeadLetterSerializer<int>(expected);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([123]))
            .Transform(
                new AlwaysFailingTransformer<int>(ErrorType.Permanent),
                new StageFailureOptions
                {
                    OnPermanentFailure = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(stream, serializer))
            .WithObserver(observer)
            .Run();

        var thrown = await FluentActions.Awaiting(
                () => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<IOException>()
            .WithMessage("dead-letter persistence failed");

        thrown.Which.Should().BeSameAs(expected);
        run.State.Should().Be(PipelineRunState.Faulted);
        run.Metrics.ItemsDeadLettered.Should().Be(0);
        observer.Events.OfType<DeadLetterWrittenEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task TransformerException_RetriesWhenRetryPolicyAllows()
    {
        var transformer = new ThrowThenSucceedTransformer();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay, retryOn: _ => true),
                })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(5);
        transformer.Attempts.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(DefaultPermanentExceptionData))]
    public async Task TransformerException_TimeoutAndHttpRemainPermanentByDefault(Exception exception)
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(new ThrowingExceptionTransformer(exception))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Type.Should().Be(ErrorType.Permanent);
        outputs[0].Result.Error!.Value.Category.Should().Be("StageException");
    }

    [Fact]
    public async Task TransformerException_CustomClassifier_CanMarkHttpExceptionTransient()
    {
        var transformer = new ThrowOnceExceptionTransformer(
            new HttpRequestException("http transient"));

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    ExceptionClassifier = ex => new SmartPipeError(
                        ex.Message,
                        ErrorType.Transient,
                        "Http",
                        ex),
                    Retry = new RetryPolicy(1, MinimalRetryDelay),
                })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        transformer.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task TransformerException_CustomClassifier_CanClassifyUserOperationCanceledException()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                new ThrowingExceptionTransformer(new OperationCanceledException("user cancelled")),
                new StageFailureOptions
                {
                    ExceptionClassifier = ex => new SmartPipeError(
                        ex.Message,
                        ErrorType.Transient,
                        "UserCancellation",
                        ex),
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.Error!.Value.Type.Should().Be(ErrorType.Transient);
        outputs[0].Result.Error!.Value.Category.Should().Be("UserCancellation");
        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task TransformerException_ClassifierThrowing_FaultsPipelineWithClassifierException()
    {
        var classifierException = new ApplicationException("classifier boom");
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                new ThrowingExceptionTransformer(new InvalidOperationException("stage exception boom")),
                new StageFailureOptions
                {
                    ExceptionClassifier = _ => throw classifierException,
                })
            .Run();

        var thrown = await FluentActions.Awaiting(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<ApplicationException>()
            .WithMessage("classifier boom");
        thrown.Which.Should().BeSameAs(classifierException);
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task TransformerException_WritesDeadLetterWhenConfigured()
    {
        await using var stream = new MemoryStream();
        var serializer = new CapturingDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                new ThrowingPolicyTransformer(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream, serializer))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        serializer.Written.Should().ContainSingle()
            .Which.Error.Category.Should().Be("StageException");
    }

    [Fact]
    public async Task TransformerException_EmitsFailureResultByDefault()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(new ThrowingPolicyTransformer())
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Category.Should().Be("StageException");
    }

    [Fact]
    public async Task TransformerException_FaultsOnlyWhenPolicyIsFaultPipeline()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                new ThrowingPolicyTransformer(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.FaultPipeline })
            .Run();

        var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await completion.Should().ThrowAsync<PipelineFailureActionException>()
            .WithMessage("*stage exception boom*");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task TransformerOperationCanceledException_RemainsCancellation()
    {
        var transformer = new BlockingTimeoutTransformer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    ExceptionClassifier = ex => new SmartPipeError(
                        ex.Message,
                        ErrorType.Transient,
                        "ShouldNotRun",
                        ex),
                })
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.CancelAsync();

        var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await completion.Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task StageExecutor_Drain_CompletesAcceptedRetryPolicy()
    {
        var transformer = new FailThenSucceedTransformer<int>(failuresBeforeSuccess: 1);

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([55]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, TimeSpan.FromMilliseconds(100)),
                })
            .Run();

        await transformer.FirstFailureReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(55);
        transformer.Attempts.Should().Equal(0, 1);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);

        return outputs;
    }

    public static IEnumerable<object[]> DefaultPermanentExceptionData()
    {
        yield return [new TimeoutException("thrown timeout")];
        yield return [new HttpRequestException("http failure")];
    }

    private sealed class EnumerablePipelineSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _items;

        public EnumerablePipelineSource(IReadOnlyList<T> items)
        {
            _items = items;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _items[i],
                    "stage-executor-tests",
                    "stage-executor-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailThenSucceedTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly int _failuresBeforeSuccess;
        private readonly ConcurrentQueue<int> _attempts = [];

        public FailThenSucceedTransformer(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public IReadOnlyCollection<int> Attempts => _attempts;

        public TaskCompletionSource FirstFailureReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            _attempts.Enqueue(envelope.Attempt);
            if (envelope.Attempt < _failuresBeforeSuccess)
                FirstFailureReturned.TrySetResult();

            return ValueTask.FromResult(
                envelope.Attempt < _failuresBeforeSuccess
                    ? StageResult<T>.Failure(new SmartPipeError(
                        "transient failure",
                        ErrorType.Transient,
                        "Transient"))
                    : StageResult<T>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AlwaysFailingTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly ErrorType _errorType;
        private readonly ConcurrentQueue<int> _attempts = [];

        public AlwaysFailingTransformer(ErrorType errorType)
        {
            _errorType = errorType;
        }

        public IReadOnlyCollection<int> Attempts => _attempts;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            _attempts.Enqueue(envelope.Attempt);
            return ValueTask.FromResult(StageResult<T>.Failure(new SmartPipeError(
                $"{_errorType} failure",
                _errorType,
                _errorType.ToString())));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingPolicyTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromException<StageResult<int>>(
                new InvalidOperationException("stage exception boom"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowThenSucceedTransformer : IPipelineTransformer<int, int>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                return ValueTask.FromException<StageResult<int>>(
                    new InvalidOperationException("stage exception boom"));
            }

            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelingPolicyTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromException<StageResult<int>>(
                new OperationCanceledException("stage cancelled"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailFirstItemTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(
                envelope.Payload == 1
                    ? StageResult<int>.Failure(new SmartPipeError(
                        "first item fails",
                        ErrorType.Permanent,
                        "FirstItem"))
                    : StageResult<int>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ClockAdvancingSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly AdvancingPipelineClock _clock;
        private readonly TimeSpan _advanceAfterFirst;

        public ClockAdvancingSource(
            IReadOnlyList<T> items,
            AdvancingPipelineClock clock,
            TimeSpan advanceAfterFirst)
        {
            _items = items;
            _clock = clock;
            _advanceAfterFirst = advanceAfterFirst;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (i == 1)
                    _clock.Advance(_advanceAfterFirst);

                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _items[i],
                    "stage-executor-tests",
                    "stage-executor-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingExceptionTransformer(Exception exception) : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromException<StageResult<int>>(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowOnceExceptionTransformer(Exception exception) : IPipelineTransformer<int, int>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                return ValueTask.FromException<StageResult<int>>(exception);

            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UtcJumpingSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly UtcJumpingPipelineClock _clock;
        private readonly TimeSpan _jumpBeforeSecond;

        public UtcJumpingSource(
            IReadOnlyList<T> items,
            UtcJumpingPipelineClock clock,
            TimeSpan jumpBeforeSecond)
        {
            _items = items;
            _clock = clock;
            _jumpBeforeSecond = jumpBeforeSecond;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (i == 1)
                    _clock.JumpUtc(_jumpBeforeSecond);

                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _items[i],
                    "stage-executor-tests",
                    "stage-executor-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdvancingPipelineClock : IPipelineClock
    {
        private DateTimeOffset _now;

        public AdvancingPipelineClock(DateTimeOffset now)
        {
            _now = now;
        }

        public DateTimeOffset GetUtcNow() => _now;

        public long GetTimestamp() => _now.UtcTicks;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class UtcJumpingPipelineClock : IPipelineClock
    {
        private DateTimeOffset _now;
        private long _timestamp;

        public UtcJumpingPipelineClock(DateTimeOffset now)
        {
            _now = now;
        }

        public DateTimeOffset GetUtcNow() => _now;

        public long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void JumpUtc(TimeSpan duration) => _now += duration;

        public void AdvanceTimestamp(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class BlockingTimeoutTransformer<T> : IPipelineTransformer<T, T>
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            return StageResult<T>.Success(envelope.Payload);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReleaseAfterCancellationTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts => Volatile.Read(ref _attempts);

        public void Release() => _release.TrySetResult();

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            Entered.TrySetResult();
            using var registration = ct.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                CancellationObserved);

            await _release.Task.ConfigureAwait(false);
            return StageResult<T>.Success(envelope.Payload);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReleasableLateAttemptTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly bool _completeRetryAttemptsImmediately;
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _attempts = [];
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _attemptCount;
        private int _maxConcurrent;
        private int _order;
        private int _lateAttemptCompletedOrder;
        private int _disposeOrder;

        public ReleasableLateAttemptTransformer(bool completeRetryAttemptsImmediately = false)
        {
            _completeRetryAttemptsImmediately = completeRetryAttemptsImmediately;
        }

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public int LateAttemptCompletedOrder => Volatile.Read(ref _lateAttemptCompletedOrder);

        public int DisposeOrder => Volatile.Read(ref _disposeOrder);

        public Task WaitForAttemptAsync(int attempt) => GetAttempt(attempt).Task;

        public void ReleaseLateAttempts() => _release.TrySetResult();

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attemptCount);
            var active = Interlocked.Increment(ref _active);
            SetMaxConcurrent(active);
            GetAttempt(envelope.Attempt).TrySetResult();

            try
            {
                if (_completeRetryAttemptsImmediately && envelope.Attempt > 0)
                    return StageResult<T>.Success(envelope.Payload);

                await _release.Task.ConfigureAwait(false);
                return StageResult<T>.Success(envelope.Payload);
            }
            finally
            {
                if (envelope.Attempt == 0)
                    Volatile.Write(
                        ref _lateAttemptCompletedOrder,
                        Interlocked.Increment(ref _order));

                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposeOrder, Interlocked.Increment(ref _order));
            return ValueTask.CompletedTask;
        }

        private TaskCompletionSource GetAttempt(int attempt) =>
            _attempts.GetOrAdd(
                attempt,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private void SetMaxConcurrent(int active)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrent);
                if (active <= observed)
                    return;

                if (Interlocked.CompareExchange(ref _maxConcurrent, active, observed) == observed)
                    return;
            }
        }
    }

    private sealed class GraceAdvancingTimeoutTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly AdvancingPipelineClock _clock;
        private readonly TimeSpan _advanceOnCancellation;
        private int _attempts;

        public GraceAdvancingTimeoutTransformer(
            AdvancingPipelineClock clock,
            TimeSpan advanceOnCancellation)
        {
            _clock = clock;
            _advanceOnCancellation = advanceOnCancellation;
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _clock.Advance(_advanceOnCancellation);
                throw;
            }

            return StageResult<T>.Success(envelope.Payload);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPipelineObserver : IPipelineObserver
    {
        private readonly ConcurrentQueue<PipelineEvent> _events = [];
        private readonly ConcurrentDictionary<Type, TaskCompletionSource> _waiters = [];

        public IReadOnlyCollection<PipelineEvent> Events => _events;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _events.Enqueue(pipelineEvent);
            if (_waiters.TryGetValue(pipelineEvent.GetType(), out var waiter))
                waiter.TrySetResult();

            return ValueTask.CompletedTask;
        }

        public Task WaitForAsync<TEvent>()
            where TEvent : PipelineEvent
        {
            if (_events.OfType<TEvent>().Any())
                return Task.CompletedTask;

            return _waiters.GetOrAdd(
                typeof(TEvent),
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task;
        }
    }

    private sealed class CapturingDeadLetterSerializer<T> : IDeadLetterSerializer<T>
    {
        private readonly List<DeadLetterEnvelope<T>> _written = [];

        public IReadOnlyList<DeadLetterEnvelope<T>> Written => _written;

        public ValueTask WriteAsync(
            DeadLetterEnvelope<T> envelope,
            Stream stream,
            CancellationToken ct = default)
        {
            _written.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var envelope in _written)
            {
                ct.ThrowIfCancellationRequested();
                yield return envelope;
                await Task.Yield();
            }
        }
    }

    private sealed class ThrowingDeadLetterSerializer<T> : IDeadLetterSerializer<T>
    {
        private readonly Exception _exception;

        public ThrowingDeadLetterSerializer(Exception exception)
        {
            _exception = exception;
        }

        public ValueTask WriteAsync(
            DeadLetterEnvelope<T> envelope,
            Stream stream,
            CancellationToken ct = default)
        {
            throw _exception;
        }

        public async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
