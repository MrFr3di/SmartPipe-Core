using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartPipe.Core;
using SmartPipe.Extensions.OpenTelemetry;

var services = new ServiceCollection();

services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddOtlpExporter())
    .WithTracing(tracing => tracing.AddOtlpExporter())
    .AddSmartPipeInstrumentation();

using var provider = services.BuildServiceProvider();
using var meterProvider = provider.GetRequiredService<MeterProvider>();
using var tracerProvider = provider.GetRequiredService<TracerProvider>();

var key = new PipelineKey("consumer-opentelemetry-otlp");
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

meterProvider.ForceFlush();
Console.WriteLine("CONSUMER_OK opentelemetry-otlp");
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
