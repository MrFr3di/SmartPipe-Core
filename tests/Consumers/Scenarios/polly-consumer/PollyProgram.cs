using Polly;
using Polly.Retry;
using SmartPipe.Consumers.Resilience;
using SmartPipe.Core;
using SmartPipe.Extensions.Polly;

// One retry per item: the application owns the Polly pipeline and its retry policy.
var retry = new ResiliencePipelineBuilder<StageResult<int>>()
    .AddRetry(new RetryStrategyOptions<StageResult<int>> { MaxRetryAttempts = 1, Delay = TimeSpan.Zero })
    .Build();

// Core composition: an owned inner transform decorated through the stage-keyed Transform API.
var owned = new FlakyMultiplier(10);
var sink = new CollectingSink();
var stageKey = new PipelineStageKey("polly-multiply");
var definition = PipelineDefinitionBuilder
    .From(new PipelineKey("consumer-polly"), ConsumerSource.Of(1, 2, 3))
    .Transform(
        stageKey,
        PollyPipelineComponents.Decorate<int, int>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(owned),
            retry,
            PollyInnerTransformOwnership.Owned,
            new PollyTransformDecoratorOptions { OperationKey = "consumer-multiply" }))
    .To(PipelineComponent.Borrowed<IPipelineSink<int>>(sink, initialize: true));

await using (var run = await definition.StartAsync().ConfigureAwait(false))
{
    await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        ConsumerCheck.Require(output.Result.IsSuccess, "The decorated pipeline published a failure.");

    await run.Completion.ConfigureAwait(false);
}

ConsumerCheck.Require(definition.Stages[0].Key == stageKey, "The Polly stage key was not preserved.");
ConsumerCheck.Require(sink.Items.Order().SequenceEqual([10, 20, 30]), "The sink did not receive the retried results.");
ConsumerCheck.Require(owned.Attempts == 6, $"Expected 6 inner attempts, observed {owned.Attempts}.");
ConsumerCheck.Require(
    owned.InitializeCount == 1 && owned.DisposeCount == 1,
    "The owned inner transform was not initialized and disposed exactly once.");

// Direct use: a borrowed inner stays alive, and the mapper sees only the final exception.
var borrowed = new AlwaysFailing();
var mapped = 0;
var decorator = new PollyTransformDecorator<int, int>(
    borrowed,
    retry,
    PollyInnerTransformOwnership.Borrowed,
    new PollyTransformDecoratorOptions
    {
        ExceptionMapper = exception =>
        {
            mapped++;
            return new SmartPipeError(exception.Message, ErrorType.Transient, "Polly", exception);
        },
    });
await using (decorator.ConfigureAwait(false))
{
    await decorator.InitializeAsync().ConfigureAwait(false);
    var result = await decorator.TransformAsync(ProcessingEnvelope<int>.Create(5)).ConfigureAwait(false);
    ConsumerCheck.Require(
        result.Kind == StageResultKind.Failure && result.Error?.Message == "attempt 2",
        "The final exception was not mapped to a failure result.");
}

ConsumerCheck.Require(borrowed.Attempts == 2 && mapped == 1, "The retry bound or final-only mapping was not honored.");
ConsumerCheck.Require(borrowed.DisposeCount == 0, "The decorator disposed a borrowed inner transform.");

Console.WriteLine("CONSUMER_OK polly");
return 0;
