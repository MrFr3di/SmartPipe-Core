using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
var smartPipe = builder.Services.AddSmartPipe();
smartPipe.AddPipeline(CreateDefinition("orders", 1)).RunAsHostedService(options => options.Order = 0);
smartPipe.AddPipeline(CreateDefinition("replay", 2)).RunAsHostedService(options => options.Order = 1);
using var host = builder.Build();
await host.StartAsync();
if (host.Services.GetServices<IHostedService>().Count(service =>
        service.GetType().Assembly.GetName().Name == "SmartPipe.Extensions.Hosting") != 1)
    return 1;
await host.StopAsync();
Console.WriteLine("CONSUMER_OK hosting-direct");
return 0;

static PipelineDefinition<int, int> CreateDefinition(string key, int value) =>
    PipelineDefinitionBuilder.From(
            new PipelineKey(key),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                    PipelineSource.FromAsyncEnumerable(Values(value)))))
        .Build();

static async IAsyncEnumerable<int> Values(
    int value,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    yield return value;
    await Task.CompletedTask;
}
