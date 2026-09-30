using Polly;
using Polly.Retry;
using SmartPipe.Core;
using static SmartPipe.Extensions.Polly.Tests.PollyTestSupport;

namespace SmartPipe.Extensions.Polly.Tests;

public sealed class PollyPipelineComponentsTests
{
    private static readonly PipelineStageKey PollyStage = new("polly-stage");

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Decorate_ValidatesFactoriesPipelineOwnershipAndOptionsAtComposition()
    {
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineTransformer<int, int>>> innerFactory =
            (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(Doubling());
        Func<PipelineActivationContext, ResiliencePipeline<StageResult<int>>> pipelineFactory = _ => Empty<int>();
        var invalidOptions = new PollyTransformDecoratorOptions { OperationKey = "not valid" };

        Assert.Throws<ArgumentNullException>("innerFactory", () => PollyPipelineComponents.Decorate<int, int>(null!, pipelineFactory, PollyInnerTransformOwnership.Owned));
        Assert.Throws<ArgumentNullException>("pipelineFactory", () => PollyPipelineComponents.Decorate(innerFactory, (Func<PipelineActivationContext, ResiliencePipeline<StageResult<int>>>)null!, PollyInnerTransformOwnership.Owned));
        Assert.Throws<ArgumentOutOfRangeException>("innerOwnership", () => PollyPipelineComponents.Decorate(innerFactory, pipelineFactory, (PollyInnerTransformOwnership)(-1)));
        Assert.Throws<ArgumentException>("options", () => PollyPipelineComponents.Decorate(innerFactory, pipelineFactory, PollyInnerTransformOwnership.Owned, invalidOptions));

        Assert.Throws<ArgumentNullException>("innerFactory", () => PollyPipelineComponents.Decorate<int, int>(null!, Empty<int>(), PollyInnerTransformOwnership.Borrowed));
        Assert.Throws<ArgumentNullException>("pipeline", () => PollyPipelineComponents.Decorate(innerFactory, (ResiliencePipeline<StageResult<int>>)null!, PollyInnerTransformOwnership.Borrowed));
        Assert.Throws<ArgumentOutOfRangeException>("innerOwnership", () => PollyPipelineComponents.Decorate(innerFactory, Empty<int>(), (PollyInnerTransformOwnership)2));
        Assert.Throws<ArgumentException>("options", () => PollyPipelineComponents.Decorate(innerFactory, Empty<int>(), PollyInnerTransformOwnership.Borrowed, invalidOptions));

        var component = PollyPipelineComponents.Decorate(innerFactory, Empty<int>(), PollyInnerTransformOwnership.Owned);
        Assert.Equal(PipelineComponentOwnership.RuntimeOwned, component.Ownership);
        Assert.True(component.Initialize);
    }

    [Fact]
    public async Task InitialTransform_PreservesStageKeyAndTypesAndOwnsInnerThroughCoreLifecycle()
    {
        var inner = Doubling();
        var sink = new CollectingSink<int>();
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(inner),
            RetryPipeline<int>(maxRetryAttempts: 2),
            PollyInnerTransformOwnership.Owned,
            new PollyTransformDecoratorOptions { OperationKey = "double" });

        PipelineDefinitionBuilder<int, int> transformed = Source(1, 2, 3).Transform(PollyStage, component);
        var definition = transformed.To(PipelineComponent.Borrowed<IPipelineSink<int>>(sink, initialize: true));
        await RunAsync(definition);

        var stage = Assert.Single(definition.Stages);
        Assert.Equal(PollyStage, stage.Key);
        Assert.Equal(typeof(int), stage.InputType);
        Assert.Equal(typeof(int), stage.OutputType);
        Assert.Equal([2, 4, 6], sink.Items.Order());
        Assert.Equal(3, inner.TransformCalls);
        Assert.Equal(1, inner.InitializeCalls);
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task TypedTransform_PreservesStageKeyAndTypesAndLeavesBorrowedInnerUndisposed()
    {
        var inner = new RecordingTransformer<string, int>((envelope, _, _) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload.Length)));
        var sink = new CollectingSink<int>();
        var typedComponent = PollyPipelineComponents.Decorate<string, int>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<string, int>>(inner),
            _ => Empty<int>(),
            PollyInnerTransformOwnership.Borrowed);

        PipelineDefinitionBuilder<int, string> typedCurrent = Source(1, 22, 333)
            .Transform(new PipelineStageKey("format"), PipelineComponent.Borrowed<IPipelineTransformer<int, string>>(new Formatting()));
        PipelineDefinitionBuilder<int, int> typedTransformed = typedCurrent.Transform(PollyStage, typedComponent);
        var definition = typedTransformed.To(PipelineComponent.Borrowed<IPipelineSink<int>>(sink, initialize: true));
        await RunAsync(definition);

        Assert.Equal(2, definition.Stages.Count);
        Assert.Equal(PollyStage, definition.Stages[1].Key);
        Assert.Equal(typeof(string), definition.Stages[1].InputType);
        Assert.Equal(typeof(int), definition.Stages[1].OutputType);
        Assert.Equal([1, 2, 3], sink.Items.Order());
        Assert.Equal(1, inner.InitializeCalls);
        Assert.Equal(0, inner.DisposeCalls);
    }

    [Fact]
    public async Task CoreRetryTimesPollyRetry_MultipliesActualInnerAttempts()
    {
        const int CoreRetries = 2;
        const int PollyRetries = 1;
        var inner = new RecordingTransformer<int, int>((_, _, _) => ThrowFromInner<int>(new TimeoutException("transient")));
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(inner),
            RetryPipeline<int>(PollyRetries),
            PollyInnerTransformOwnership.Owned);
        var failureOptions = new StageFailureOptions
        {
            Retry = new RetryPolicy(CoreRetries, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), BackoffStrategy.Fixed),
            ExceptionClassifier = exception => new SmartPipeError(exception.Message, ErrorType.Transient, "Test", exception),
        };
        var definition = Source(1).Transform(PollyStage, component, failureOptions).Build();

        var results = await RunAsync(definition);

        Assert.True(Assert.Single(results).IsFailure);
        Assert.Equal((1 + CoreRetries) * (1 + PollyRetries), inner.TransformCalls);
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task PipelineFactory_RunsBeforeInnerAcquisitionForEachActivation()
    {
        var log = new EventLog();
        var inner = new RecordingTransformer<int, int>(DoublingBehavior, log);
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) =>
            {
                log.Add("inner-factory");
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(inner);
            },
            _ =>
            {
                log.Add("pipeline-factory");
                return Empty<int>();
            },
            PollyInnerTransformOwnership.Borrowed);

        await RunAsync(Source(1).Transform(PollyStage, component).Build());

        Assert.Equal(["pipeline-factory", "inner-factory", "inner-initialize-start", "inner-initialize-end", "inner-transform-1"], log.Events);
    }

    [Fact]
    public async Task PipelineFactoryFailureOrNullResult_NeverAcquiresInner()
    {
        var innerFactoryCalls = 0;
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineTransformer<int, int>>> innerFactory = (_, _) =>
        {
            Interlocked.Increment(ref innerFactoryCalls);
            return ValueTask.FromResult<IPipelineTransformer<int, int>>(Doubling());
        };
        var failure = new InvalidOperationException("pipeline factory");
        var throwing = PollyPipelineComponents.Decorate(innerFactory, _ => throw failure, PollyInnerTransformOwnership.Owned);
        var returningNull = PollyPipelineComponents.Decorate(innerFactory, _ => null!, PollyInnerTransformOwnership.Owned);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Source(1).Transform(PollyStage, throwing).Build().StartAsync(TestToken));
        Assert.Contains(failure, Chain(thrown));
        var nullError = await Assert.ThrowsAnyAsync<Exception>(() => Source(1).Transform(PollyStage, returningNull).Build().StartAsync(TestToken));
        Assert.Contains(Chain(nullError), exception => exception is InvalidOperationException && exception.Message.Contains("pipeline factory", StringComparison.Ordinal));
        Assert.Equal(0, innerFactoryCalls);
    }

    [Fact]
    public async Task InnerFactoryReturningNull_FailsActivation()
    {
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(null!),
            Empty<int>(),
            PollyInnerTransformOwnership.Owned);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Source(1).Transform(PollyStage, component).Build().StartAsync(TestToken));

        Assert.Contains(Chain(error), exception => exception is InvalidOperationException && exception.Message.Contains("inner transform factory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PollyInnerTransformOwnership.Owned, 1)]
    [InlineData(PollyInnerTransformOwnership.Borrowed, 0)]
    public async Task InnerInitializationFailure_RollsBackThroughCoreWithoutDoubleDispose(
        PollyInnerTransformOwnership ownership,
        int expectedDisposeCalls)
    {
        var failure = new InvalidOperationException("inner init");
        var inner = new RecordingTransformer<int, int>(DoublingBehavior)
        {
            OnInitialize = _ => ValueTask.FromException(failure),
        };
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(inner),
            Empty<int>(),
            ownership);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Source(1).Transform(PollyStage, component).Build().StartAsync(TestToken));

        Assert.Contains(failure, Chain(error));
        Assert.Equal(1, inner.InitializeCalls);
        Assert.Equal(expectedDisposeCalls, inner.DisposeCalls);
    }

    [Theory]
    [InlineData(PollyInnerTransformOwnership.Owned, 1)]
    [InlineData(PollyInnerTransformOwnership.Borrowed, 0)]
    public async Task CancellationAfterInnerAcquisition_DisposesOnlyAnOwnedInner(
        PollyInnerTransformOwnership ownership,
        int expectedDisposeCalls)
    {
        using var start = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var inner = Doubling();
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) =>
            {
                start.Cancel();
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(inner);
            },
            Empty<int>(),
            ownership);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Source(1).Transform(PollyStage, component).Build().StartAsync(start.Token));

        Assert.Contains(Chain(error), exception => exception is OperationCanceledException);
        Assert.Equal(0, inner.InitializeCalls);
        Assert.Equal(expectedDisposeCalls, inner.DisposeCalls);
    }

    [Fact]
    public async Task CleanupFailureAfterFailedActivation_IsAppendedAfterThePrimary()
    {
        using var start = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var cleanup = new InvalidOperationException("inner dispose");
        var inner = new RecordingTransformer<int, int>(DoublingBehavior) { OnDispose = () => ValueTask.FromException(cleanup) };
        var component = PollyPipelineComponents.Decorate<int, int>(
            (_, _) =>
            {
                start.Cancel();
                return ValueTask.FromResult<IPipelineTransformer<int, int>>(inner);
            },
            Empty<int>(),
            PollyInnerTransformOwnership.Owned);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Source(1).Transform(PollyStage, component).Build().StartAsync(start.Token));

        var aggregate = Assert.Single(Chain(error).OfType<AggregateException>(), exception => exception.InnerExceptions.Contains(cleanup));
        Assert.IsAssignableFrom<OperationCanceledException>(aggregate.InnerExceptions[0]);
        Assert.Same(cleanup, aggregate.InnerExceptions[1]);
        Assert.Equal(1, inner.DisposeCalls);
    }

    private static PipelineDefinitionBuilder<int> Source(params int[] items) =>
        PipelineDefinitionBuilder.From(
            new PipelineKey("polly-tests"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                ValueTask.FromResult<IPipelineSource<int>>(new ListSource<int>(items))));

    private static async Task<IReadOnlyList<PipelineResult<TOutput>>> RunAsync<TInput, TOutput>(PipelineDefinition<TInput, TOutput> definition)
    {
        var results = new List<PipelineResult<TOutput>>();
        await using var run = await definition.StartAsync(TestToken);
        await foreach (var output in run.Outputs.ReadAllAsync(TestToken))
            results.Add(output.Result);

        await run.Completion;
        return results;
    }

    private static RecordingTransformer<int, int> Doubling() => new(DoublingBehavior);

    private static ValueTask<StageResult<int>> DoublingBehavior(ProcessingEnvelope<int> envelope, int attempt, CancellationToken ct) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload * 2));

    private static ResiliencePipeline<StageResult<T>> RetryPipeline<T>(int maxRetryAttempts) =>
        new ResiliencePipelineBuilder<StageResult<T>>()
            .AddRetry(new RetryStrategyOptions<StageResult<T>> { MaxRetryAttempts = maxRetryAttempts, Delay = TimeSpan.Zero })
            .Build();

    private static List<Exception> Chain(Exception root)
    {
        var found = new List<Exception>();
        var pending = new Stack<Exception>([root]);
        while (pending.TryPop(out var current))
        {
            found.Add(current);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }

            if (current is PipelineActivationException activation)
            {
                foreach (var cleanup in activation.CleanupExceptions)
                    pending.Push(cleanup);
            }
        }

        return found;
    }

    private sealed class Formatting : IPipelineTransformer<int, string>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<string>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<string>.Success(new string('x', envelope.Payload.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
