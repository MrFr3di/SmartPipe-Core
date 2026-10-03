using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetryFacade;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.OpenTelemetry;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.HealthChecks;

var builder = Host.CreateApplicationBuilder();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(SmartPipeDiagnostics.MeterName))
    .AddSmartPipeInstrumentation();

builder.Services.AddScoped<FacadeSource>();
builder.Services.AddScoped<FacadeStage>();
builder.Services.AddScoped<FacadeSink>();
builder.Services.AddSmartPipeHostedService<int, int>(
    "opentelemetry-facade",
    pipeline => pipeline
        .UseSource<FacadeSource>()
        .UseStage<FacadeStage>()
        .UseSink<FacadeSink>());

var healthKey = new PipelineKey("bundle-health");
var healthDefinition = PipelineDefinitionBuilder.From(healthKey,
    PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
        static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new FacadeSource())))
    .Build();
builder.Services.AddSmartPipe().AddPipeline(healthDefinition).AddLiveness();

using var host = builder.Build();
await host.StartAsync();
var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
if (report.Entries[SmartPipeHealthCheckNames.Liveness(healthKey)].Status != HealthStatus.Healthy)
    throw new InvalidOperationException("Canonical HealthChecks API through the facade bundle failed.");
await host.StopAsync();
Console.WriteLine("CONSUMER_OK opentelemetry-facade");
return 0;

namespace OpenTelemetryFacade
{
    internal sealed class FacadeSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(1, "opentelemetry-facade", "run", 1);
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FacadeStage : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FacadeSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
