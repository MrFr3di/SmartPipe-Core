using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.HealthChecks;
using HealthChecksDirect;

var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
var builder = services.AddSmartPipe();
var orders = builder.AddPipeline(Definition("orders"));
var replay = builder.AddPipeline(Definition("replay"));
orders.AddLiveness().AddReadiness();
replay.AddLiveness().AddReadiness();
services.AddHealthChecks()
    .AddSmartPipeAggregateLiveness()
    .AddSmartPipeAggregateReadiness();
await using var provider = services.BuildServiceProvider(
    new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
if (report.Entries[SmartPipeHealthCheckNames.Liveness(orders.Key)].Status != HealthStatus.Healthy
    || report.Entries[SmartPipeHealthCheckNames.Liveness(replay.Key)].Status != HealthStatus.Healthy
    || report.Entries[SmartPipeHealthCheckNames.Readiness(orders.Key)].Status != HealthStatus.Unhealthy
    || report.Entries[SmartPipeHealthCheckNames.Readiness(replay.Key)].Status != HealthStatus.Unhealthy
    || report.Entries[SmartPipeHealthCheckNames.AggregateLiveness].Status != HealthStatus.Healthy
    || report.Entries[SmartPipeHealthCheckNames.AggregateReadiness].Status != HealthStatus.Unhealthy)
{
    return 1;
}

Console.WriteLine("CONSUMER_OK health-checks-direct");
return 0;

static PipelineDefinition<int, int> Definition(string key) =>
    PipelineDefinitionBuilder.From(
            new PipelineKey(key),
            PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
        .Build();

namespace HealthChecksDirect
{
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
}
