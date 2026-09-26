using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Hedging;
using Polly.Retry;
using Polly.Timeout;
using SmartPipe.Core;
using static SmartPipe.Extensions.Polly.Tests.PollyTestSupport;

namespace SmartPipe.Extensions.Polly.Tests;

public sealed class PollyTransformDecoratorExecutionTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public static TheoryData<string> ResultKinds => new() { "success", "failure", "filtered", "cancelled", "timed-out" };

    [Fact]
    public async Task EmptyPipeline_InvokesTheRealInnerOnceWithTheCallerToken()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var inner = new RecordingTransformer<int, string>((envelope, _, _) =>
            ValueTask.FromResult(StageResult<string>.Success($"value-{envelope.Payload}")));
        await using var decorator = await InitializedAsync(inner, Empty<string>());
        var envelope = Envelope(7);

        var result = await decorator.TransformAsync(envelope, caller.Token);

        Assert.Equal(StageResult<string>.Success("value-7"), result);
        Assert.Equal(1, inner.TransformCalls);
        Assert.Same(envelope, Assert.Single(inner.Envelopes));
        Assert.Equal(caller.Token, Assert.Single(inner.Tokens));
    }

    [Theory]
    [MemberData(nameof(ResultKinds))]
    public async Task FinalResult_PassesThroughUnchanged(string kind)
    {
        var expected = kind switch
        {
            "success" => StageResult<string>.Success("ok"),
            "failure" => StageResult<string>.Failure(new SmartPipeError("bad", ErrorType.Permanent, "Test")),
            "filtered" => StageResult<string>.Filtered(),
            "cancelled" => StageResult<string>.Cancelled(),
            _ => StageResult<string>.TimedOut(new SmartPipeError("slow", ErrorType.Transient, "Timeout")),
        };
        var inner = new RecordingTransformer<int, string>((_, _, _) => ValueTask.FromResult(expected));
        var retry = RetryPipeline<string>(maxRetryAttempts: 3);
        await using var decorator = await InitializedAsync(inner, retry);

        var result = await decorator.TransformAsync(Envelope(1), TestToken);

        Assert.Equal(expected, result);
        Assert.Equal(1, inner.TransformCalls);
    }

    [Fact]
    public async Task EarlySuccess_StopsRetrying()
    {
        var inner = new RecordingTransformer<int, int>((envelope, _, _) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload)));
        await using var decorator = await InitializedAsync(inner, RetryPipeline<int>(maxRetryAttempts: 5));

        var result = await decorator.TransformAsync(Envelope(3), TestToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, inner.TransformCalls);
    }

    [Fact]
    public async Task ExceptionThenSuccess_CallsInnerOncePerAttempt()
    {
        var inner = new RecordingTransformer<int, int>((envelope, attempt, _) => attempt < 3
            ? ThrowFromInner<int>(new InvalidOperationException($"attempt {attempt}"))
            : ValueTask.FromResult(StageResult<int>.Success(envelope.Payload * attempt)));
        await using var decorator = await InitializedAsync(inner, RetryPipeline<int>(maxRetryAttempts: 5));
        var envelope = Envelope(2);

        var result = await decorator.TransformAsync(envelope, TestToken);

        Assert.Equal(StageResult<int>.Success(6), result);
        Assert.Equal(3, inner.TransformCalls);
        Assert.All(inner.Envelopes, seen => Assert.Same(envelope, seen));
    }

    [Fact]
    public async Task TerminalException_StaysWithinBoundAndPreservesIdentityAndStack()
    {
        var thrown = new List<Exception>();
        var inner = new RecordingTransformer<int, int>((_, attempt, _) =>
        {
            var exception = new InvalidOperationException($"attempt {attempt}");
            lock (thrown) thrown.Add(exception);
            return ThrowFromInner<int>(exception);
        });
        await using var decorator = await InitializedAsync(inner, RetryPipeline<int>(maxRetryAttempts: 2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());

        Assert.Equal(3, inner.TransformCalls);
        Assert.Same(thrown[^1], error);
        Assert.Contains(nameof(ThrowFromInner), error.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureResults_AreRetriedOnlyUnderAnExplicitResultPredicate()
    {
        var failure = StageResult<int>.Failure(new SmartPipeError("transient", ErrorType.Transient, "Test"));
        var withoutPredicate = new RecordingTransformer<int, int>((_, _, _) => ValueTask.FromResult(failure));
        await using (var decorator = await InitializedAsync(withoutPredicate, RetryPipeline<int>(maxRetryAttempts: 3)))
        {
            Assert.Equal(failure, await decorator.TransformAsync(Envelope(1), TestToken));
        }

        var withPredicate = new RecordingTransformer<int, int>((_, attempt, _) =>
            ValueTask.FromResult(attempt < 3 ? failure : StageResult<int>.Success(attempt)));
        var resultRetry = new ResiliencePipelineBuilder<StageResult<int>>()
            .AddRetry(new RetryStrategyOptions<StageResult<int>>
            {
                ShouldHandle = new PredicateBuilder<StageResult<int>>().HandleResult(result => result.Kind == StageResultKind.Failure),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            })
            .Build();
        await using (var decorator = await InitializedAsync(withPredicate, resultRetry))
        {
            Assert.Equal(StageResult<int>.Success(3), await decorator.TransformAsync(Envelope(1), TestToken));
        }

        Assert.Equal(1, withoutPredicate.TransformCalls);
        Assert.Equal(3, withPredicate.TransformCalls);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesTheOriginalExceptionAndTokenWithoutRetry()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        OperationCanceledException? thrown = null;
        var inner = new RecordingTransformer<int, int>((_, _, ct) =>
        {
            caller.Cancel();
            thrown = new OperationCanceledException("inner observed cancellation", ct);
            return ThrowFromInner<int>(thrown);
        });
        await using var decorator = await InitializedAsync(inner, RetryPipeline<int>(maxRetryAttempts: 3));

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => decorator.TransformAsync(Envelope(1), caller.Token).AsTask());

        Assert.Same(thrown, error);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(1, inner.TransformCalls);
    }

    [Fact]
    public async Task PollyTimeout_RaisesTimeoutRejectedWhileTheCallerTokenStaysActive()
    {
        var time = new SignalingTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new RecordingTransformer<int, int>(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return StageResult<int>.Success(1);
        });
        var pipeline = new ResiliencePipelineBuilder<StageResult<int>> { TimeProvider = time }
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(5) })
            .Build();
        await using var decorator = await InitializedAsync(inner, pipeline);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

        var execution = decorator.TransformAsync(Envelope(1), caller.Token).AsTask();
        await started.Task.WaitAsync(TestToken);
        await time.TimerCreated.WaitAsync(TestToken);
        time.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => execution);
        Assert.False(caller.IsCancellationRequested);
        Assert.NotEqual(caller.Token, Assert.Single(inner.Tokens));
    }

    [Fact]
    public async Task CallerCancellation_UnderPollyTimeout_IsNotConvertedToTimeout()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new RecordingTransformer<int, int>(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return StageResult<int>.Success(1);
        });
        var pipeline = new ResiliencePipelineBuilder<StageResult<int>> { TimeProvider = new SignalingTimeProvider() }
            .AddTimeout(TimeSpan.FromMinutes(5))
            .Build();
        await using var decorator = await InitializedAsync(inner, pipeline);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

        var execution = decorator.TransformAsync(Envelope(1), caller.Token).AsTask();
        await started.Task.WaitAsync(TestToken);
        await caller.CancelAsync();

        // The timeout strategy owns the final cancellation outcome; the decorator rethrows it unchanged.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.IsNotType<TimeoutRejectedException>(error);
        Assert.True(Assert.Single(inner.Tokens).IsCancellationRequested);
        Assert.Equal(1, inner.TransformCalls);
    }

    [Fact]
    public async Task OpenCircuit_PreventsInnerCalls()
    {
        var inner = new RecordingTransformer<int, int>((_, _, _) => ThrowFromInner<int>(new InvalidOperationException("down")));
        var pipeline = new ResiliencePipelineBuilder<StageResult<int>> { TimeProvider = new SignalingTimeProvider() }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<StageResult<int>>
            {
                FailureRatio = 1.0,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromMinutes(1),
                BreakDuration = TimeSpan.FromMinutes(1),
            })
            .Build();
        await using var decorator = await InitializedAsync(inner, pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(2), TestToken).AsTask());
        await Assert.ThrowsAsync<BrokenCircuitException>(() => decorator.TransformAsync(Envelope(3), TestToken).AsTask());

        Assert.Equal(2, inner.TransformCalls);
    }

    [Fact]
    public async Task IsolatedCircuit_PreservesPollyExceptionType()
    {
        var control = new CircuitBreakerManualControl();
        var inner = new RecordingTransformer<int, int>((_, _, _) => ValueTask.FromResult(StageResult<int>.Success(1)));
        var pipeline = new ResiliencePipelineBuilder<StageResult<int>>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<StageResult<int>> { ManualControl = control })
            .Build();
        await using var decorator = await InitializedAsync(inner, pipeline);
        await control.IsolateAsync(TestToken);

        await Assert.ThrowsAsync<IsolatedCircuitException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
        Assert.Equal(0, inner.TransformCalls);
    }

    [Fact]
    public async Task Fallback_ReturnsTheExactFallbackResult()
    {
        var fallback = StageResult<string>.Success("fallback");
        var inner = new RecordingTransformer<int, string>((_, _, _) => ThrowFromInner<string>(new InvalidOperationException("primary")));
        var pipeline = new ResiliencePipelineBuilder<StageResult<string>>()
            .AddFallback(new FallbackStrategyOptions<StageResult<string>>
            {
                ShouldHandle = new PredicateBuilder<StageResult<string>>().Handle<InvalidOperationException>(),
                FallbackAction = _ => Outcome.FromResultAsValueTask(fallback),
            })
            .Build();
        await using var decorator = await InitializedAsync(inner, pipeline);

        var result = await decorator.TransformAsync(Envelope(1), TestToken);

        Assert.Equal(fallback, result);
        Assert.Equal(1, inner.TransformCalls);
    }

    [Fact]
    public async Task Hedging_ReusesTheSameEnvelopeWithoutCloning()
    {
        // Hedging is safe only for reentrant, side-effect-free, or externally synchronized inner transforms.
        var inner = new RecordingTransformer<int, int>((envelope, attempt, _) => attempt == 1
            ? ThrowFromInner<int>(new InvalidOperationException("primary"))
            : ValueTask.FromResult(StageResult<int>.Success(envelope.Payload)));
        var pipeline = new ResiliencePipelineBuilder<StageResult<int>>()
            .AddHedging(new HedgingStrategyOptions<StageResult<int>>
            {
                MaxHedgedAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(-1),
            })
            .Build();
        await using var decorator = await InitializedAsync(inner, pipeline);
        var envelope = Envelope(11);

        var result = await decorator.TransformAsync(envelope, TestToken);

        Assert.Equal(StageResult<int>.Success(11), result);
        Assert.Equal(2, inner.TransformCalls);
        Assert.All(inner.Envelopes, seen => Assert.Same(envelope, seen));
    }

    [Fact]
    public async Task Mapper_RunsOnlyForTheFinalException()
    {
        var thrown = new List<Exception>();
        var mapped = new List<Exception>();
        var inner = new RecordingTransformer<int, int>((_, attempt, _) =>
        {
            var exception = new InvalidOperationException($"attempt {attempt}");
            lock (thrown) thrown.Add(exception);
            return ThrowFromInner<int>(exception);
        });
        var options = new PollyTransformDecoratorOptions
        {
            ExceptionMapper = exception =>
            {
                lock (mapped) mapped.Add(exception);
                return new SmartPipeError("mapped", ErrorType.Permanent, "Polly", exception);
            },
        };
        await using var decorator = await InitializedAsync(inner, RetryPipeline<int>(maxRetryAttempts: 2), options);

        var result = await decorator.TransformAsync(Envelope(1), TestToken);

        Assert.Equal(StageResultKind.Failure, result.Kind);
        Assert.Equal("mapped", result.Error!.Value.Message);
        Assert.Same(thrown[^1], result.Error.Value.InnerException);
        Assert.Same(thrown[^1], Assert.Single(mapped));
        Assert.Equal(3, inner.TransformCalls);
    }

    [Fact]
    public async Task Mapper_NeverSeesFailureResultsOrCancellation()
    {
        var mapperCalls = 0;
        var options = new PollyTransformDecoratorOptions
        {
            ExceptionMapper = _ =>
            {
                Interlocked.Increment(ref mapperCalls);
                return new SmartPipeError("mapped", ErrorType.Permanent);
            },
        };
        var failure = StageResult<int>.Failure(new SmartPipeError("inner", ErrorType.Permanent));
        var failing = new RecordingTransformer<int, int>((_, _, _) => ValueTask.FromResult(failure));
        await using (var decorator = await InitializedAsync(failing, Empty<int>(), options))
        {
            Assert.Equal(failure, await decorator.TransformAsync(Envelope(1), TestToken));
        }

        var cancellation = new OperationCanceledException("cancelled");
        var cancelling = new RecordingTransformer<int, int>((_, _, _) => ThrowFromInner<int>(cancellation));
        await using (var decorator = await InitializedAsync(cancelling, Empty<int>(), options))
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
            Assert.Same(cancellation, error);
        }

        Assert.Equal(0, mapperCalls);
    }

    [Fact]
    public async Task Mapper_ReturningNull_RethrowsTheOriginalException()
    {
        var original = new InvalidOperationException("original");
        var inner = new RecordingTransformer<int, int>((_, _, _) => ThrowFromInner<int>(original));
        var options = new PollyTransformDecoratorOptions { ExceptionMapper = _ => null };
        await using var decorator = await InitializedAsync(inner, Empty<int>(), options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());

        Assert.Same(original, error);
    }

    [Fact]
    public async Task MapperFailure_PreservesTheOriginalFirstAndReturnsTheContext()
    {
        var original = new InvalidOperationException("original");
        var mapperFailure = new FormatException("mapper");
        var inner = new RecordingTransformer<int, int>((_, _, _) => ThrowFromInner<int>(original));
        var operationKey = UniqueOperationKey();
        var (pipeline, capture) = Capturing<int>();
        var options = new PollyTransformDecoratorOptions { OperationKey = operationKey, ExceptionMapper = _ => throw mapperFailure };
        await using var decorator = await InitializedAsync(inner, pipeline, options);

        var error = await Assert.ThrowsAsync<AggregateException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());

        Assert.Equal([original, mapperFailure], error.InnerExceptions);
        AssertReturned(capture, operationKey);
    }

    [Fact]
    public async Task PooledContext_CarriesOnlyOperationKeyAndTokenAndIsReturnedAfterSuccess()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var operationKey = UniqueOperationKey();
        var (pipeline, capture) = Capturing<string>();
        var inner = new RecordingTransformer<string, string>((envelope, _, _) =>
            ValueTask.FromResult(StageResult<string>.Success(envelope.Payload)));
        await using var decorator = await InitializedAsync(inner, pipeline, new PollyTransformDecoratorOptions { OperationKey = operationKey });

        var result = await decorator.TransformAsync(Envelope("secret-payload"), caller.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(operationKey, capture.OperationKeyDuringExecution);
        Assert.Equal(caller.Token, capture.TokenDuringExecution);
        Assert.False(capture.ContinueOnCapturedContextDuringExecution);
        Assert.Equal(0, capture.PropertyCountDuringExecution);
        AssertReturned(capture, operationKey);
    }

    [Fact]
    public async Task PooledContext_IsReturnedAfterCancellation()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var operationKey = UniqueOperationKey();
        var (pipeline, capture) = Capturing<int>();
        var inner = new RecordingTransformer<int, int>((_, _, ct) =>
        {
            caller.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(StageResult<int>.Success(1));
        });
        await using var decorator = await InitializedAsync(inner, pipeline, new PollyTransformDecoratorOptions { OperationKey = operationKey });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decorator.TransformAsync(Envelope(1), caller.Token).AsTask());

        AssertReturned(capture, operationKey);
    }

    [Fact]
    public async Task PooledContext_IsReturnedAfterStrategyFault()
    {
        var strategyFault = new InvalidOperationException("strategy");
        var operationKey = UniqueOperationKey();
        var (pipeline, capture) = Capturing<int>(strategyFault);
        var inner = new RecordingTransformer<int, int>((_, _, _) => ValueTask.FromResult(StageResult<int>.Success(1)));
        await using var decorator = await InitializedAsync(inner, pipeline, new PollyTransformDecoratorOptions { OperationKey = operationKey });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());

        Assert.Same(strategyFault, error);
        Assert.Equal(0, inner.TransformCalls);
        AssertReturned(capture, operationKey);
    }

    [Fact]
    public async Task LateCallerCancellation_DoesNotReplaceACompletedResult()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var inner = new RecordingTransformer<int, int>((envelope, _, _) =>
        {
            caller.Cancel();
            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
        });
        await using var decorator = await InitializedAsync(inner, Empty<int>());

        var result = await decorator.TransformAsync(Envelope(5), caller.Token);

        Assert.Equal(StageResult<int>.Success(5), result);
        Assert.True(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task Construction_ValidatesArgumentsAndOperationKeyBounds()
    {
        var inner = new RecordingTransformer<int, int>((_, _, _) => ValueTask.FromResult(StageResult<int>.Success(1)));
        var pipeline = Empty<int>();

        Assert.Throws<ArgumentNullException>("inner", () => new PollyTransformDecorator<int, int>(null!, pipeline, PollyInnerTransformOwnership.Owned));
        Assert.Throws<ArgumentNullException>("pipeline", () => new PollyTransformDecorator<int, int>(inner, null!, PollyInnerTransformOwnership.Owned));
        Assert.Throws<ArgumentOutOfRangeException>("innerOwnership", () => new PollyTransformDecorator<int, int>(inner, pipeline, (PollyInnerTransformOwnership)2));
        foreach (var invalid in new[] { "", " ", "has space", "tab\tkey", "control\u0001", new string('k', 129) })
        {
            Assert.Throws<ArgumentException>("options", () => new PollyTransformDecorator<int, int>(
                inner, pipeline, PollyInnerTransformOwnership.Owned, new PollyTransformDecoratorOptions { OperationKey = invalid }));
        }

        var mapperCalls = 0;
        var options = new PollyTransformDecoratorOptions
        {
            OperationKey = new string('k', 128),
            ExceptionMapper = _ =>
            {
                mapperCalls++;
                return null;
            },
        };
        var throwing = new RecordingTransformer<int, int>((_, _, _) => ThrowFromInner<int>(new InvalidOperationException()));
        await using var decorator = new PollyTransformDecorator<int, int>(throwing, pipeline, PollyInnerTransformOwnership.Owned, options);
        await decorator.InitializeAsync(TestToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
        Assert.Equal(1, mapperCalls);
        Assert.Throws<ArgumentNullException>("envelope", () => decorator.TransformAsync(null!, TestToken));
    }

    private static ResiliencePipeline<StageResult<T>> RetryPipeline<T>(int maxRetryAttempts) =>
        new ResiliencePipelineBuilder<StageResult<T>>()
            .AddRetry(new RetryStrategyOptions<StageResult<T>>
            {
                MaxRetryAttempts = maxRetryAttempts,
                Delay = TimeSpan.Zero,
            })
            .Build();

    private static string UniqueOperationKey() => $"op-{Guid.NewGuid():N}";

    private static void AssertReturned<T>(ContextCaptureStrategy<T> capture, string operationKey)
    {
        var context = Assert.IsType<ResilienceContext>(capture.Context);
        Assert.NotEqual(operationKey, context.OperationKey);
    }
}
