using SmartPipe.Core;

var definition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("consumer-core-nativeaot"),
        PipelineComponent.RuntimeOwned<IPipelineSource<int>>(CreateSource))
    .Transform(
        new PipelineStageKey("increment"),
        PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(CreateTransformer))
    .Build();

await using var run = await definition.StartAsync();
var value = 0;
await foreach (var output in run.Outputs.ReadAllAsync())
{
    if (output.Result.IsSuccess)
        value = output.Result.Value;
}

await run.Completion;
if (value != 42)
    return 1;

Console.WriteLine("CONSUMER_OK core-nativeaot");
return 0;

static ValueTask<IPipelineSource<int>> CreateSource(
    PipelineActivationContext context,
    CancellationToken cancellationToken) =>
    ValueTask.FromResult<IPipelineSource<int>>(
        PipelineSource.FromAsyncEnumerable(Values()));

static ValueTask<IPipelineTransformer<int, int>> CreateTransformer(
    PipelineActivationContext context,
    CancellationToken cancellationToken) =>
    ValueTask.FromResult<IPipelineTransformer<int, int>>(
        PipelineTransformer.FromFunc<int, int>(
            static (value, _) => ValueTask.FromResult(value + 1)));

static async IAsyncEnumerable<int> Values()
{
    yield return 41;
    await Task.CompletedTask;
}
