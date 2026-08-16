using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartPipe.Core;
using SmartPipe.Extensions.OpenTelemetry;

var services = new ServiceCollection();

var builder = services.AddOpenTelemetry().AddSmartPipeInstrumentation();
var sameBuilder = builder.AddSmartPipeInstrumentation();
if (!ReferenceEquals(builder, sameBuilder))
    return 1;

using var provider = services.BuildServiceProvider();
if (provider.GetService<MeterProvider>() is not null || provider.GetService<TracerProvider>() is not null)
    return 1;

var key = new PipelineKey("consumer-opentelemetry-direct");
var definition = PipelineDefinitionBuilder
    .From(key, PipelineComponent.RuntimeOwned<IPipelineSource<int>>(CreateSource))
    .Transform(
        new PipelineStageKey("double"),
        PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(CreateTransformer))
    .Build();

await using var run = await definition.StartAsync();
var values = new List<int>();
await foreach (var output in run.Outputs.ReadAllAsync())
{
    if (output.Result.IsSuccess)
        values.Add(output.Result.Value);
}

await run.Completion;
if (run.PipelineKey != key || !values.SequenceEqual([2, 4, 6]))
    return 1;

Console.WriteLine("CONSUMER_OK opentelemetry-direct");
return 0;

static ValueTask<IPipelineSource<int>> CreateSource(
    PipelineActivationContext context,
    CancellationToken cancellationToken) =>
    ValueTask.FromResult<IPipelineSource<int>>(PipelineSource.FromAsyncEnumerable(Values()));

static ValueTask<IPipelineTransformer<int, int>> CreateTransformer(
    PipelineActivationContext context,
    CancellationToken cancellationToken) =>
    ValueTask.FromResult<IPipelineTransformer<int, int>>(
        PipelineTransformer.FromFunc<int, int>(
            static (value, _) => ValueTask.FromResult(value * 2)));

static async IAsyncEnumerable<int> Values()
{
    yield return 1;
    yield return 2;
    yield return 3;
    await Task.CompletedTask;
}
