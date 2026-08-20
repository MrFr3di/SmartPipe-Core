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
if (!await SmartPipe.ConsumerScenarios.ConsumerPipelineSmoke.RunAsync(key))
    return 1;

meterProvider.ForceFlush();
Console.WriteLine("CONSUMER_OK opentelemetry-otlp");
return 0;
