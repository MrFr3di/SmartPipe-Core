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
if (!await ConsumerPipelineSmoke.RunAsync(key))
    return 1;

Console.WriteLine("CONSUMER_OK opentelemetry-direct");
return 0;
