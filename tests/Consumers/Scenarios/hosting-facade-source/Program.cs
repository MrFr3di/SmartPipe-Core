using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddScoped<LegacySource>();
builder.Services.AddScoped<LegacyStage>();
builder.Services.AddScoped<LegacySink>();
builder.Services.AddSmartPipeHostedService<int, int>(
    "legacy-hosted",
    pipeline => pipeline
        .UseSource<LegacySource>()
        .UseStage<LegacyStage>()
        .UseSink<LegacySink>());
using var host = builder.Build();
await host.StartAsync();
await host.StopAsync();
Console.WriteLine("CONSUMER_OK hosting-facade-source");

internal sealed class LegacySource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return ProcessingEnvelope<int>.Create(1, "legacy-hosted", "run", 1);
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class LegacyStage : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class LegacySink : IPipelineSink<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
