using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetryFacade;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.OpenTelemetry;

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

using var host = builder.Build();
await host.StartAsync();
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
