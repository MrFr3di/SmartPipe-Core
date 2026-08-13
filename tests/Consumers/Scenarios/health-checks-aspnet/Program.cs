using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
var registration = builder.Services.AddSmartPipe().AddPipeline(
    PipelineDefinitionBuilder.From(
            new PipelineKey("web"),
            PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
        .Build());
registration.AddLiveness().AddReadiness();
var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(SmartPipeHealthCheckTags.Liveness),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(SmartPipeHealthCheckTags.Readiness),
});

Console.WriteLine("CONSUMER_OK health-checks-aspnet");
await app.DisposeAsync();

internal sealed class EmptySource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
