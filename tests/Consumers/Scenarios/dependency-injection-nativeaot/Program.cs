using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

var key = new PipelineKey("dependency-injection-nativeaot");
var services = new ServiceCollection();
services.AddSmartPipe().AddPipeline(PipelineDefinitionBuilder
    .From(
        key,
        PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
            static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                PipelineSource.FromAsyncEnumerable(Values()))))
    .Build());
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var run = await provider.GetRequiredService<ISmartPipeFactoryProvider>().GetFactory<int, int>(key).StartAsync();
await foreach (var _ in run.Outputs.ReadAllAsync()) { }
await run.Completion;
Console.WriteLine("CONSUMER_OK dependency-injection-nativeaot");

static async IAsyncEnumerable<int> Values()
{
    yield return 1;
    await Task.CompletedTask;
}
