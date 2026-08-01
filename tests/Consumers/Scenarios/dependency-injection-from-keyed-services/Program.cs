using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

const string keyValue = "from-keyed";
var services = new ServiceCollection();
services.AddSmartPipe().AddPipeline(PipelineDefinitionBuilder
    .From(
        new PipelineKey(keyValue),
        PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
            static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                PipelineSource.FromAsyncEnumerable(Values()))))
    .Build());
services.AddSingleton<KeyedStarter>();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
if (await provider.GetRequiredService<KeyedStarter>().RunAsync() != 7)
    return 1;

Console.WriteLine("CONSUMER_OK dependency-injection-from-keyed-services");
return 0;

static async IAsyncEnumerable<int> Values()
{
    yield return 7;
    await Task.CompletedTask;
}

internal sealed class KeyedStarter([FromKeyedServices("from-keyed")] ISmartPipeRunFactory<int, int> factory)
{
    public async Task<int> RunAsync()
    {
        await using var run = await factory.StartAsync();
        await foreach (var output in run.Outputs.ReadAllAsync())
            if (output.Result.IsSuccess)
                return output.Result.Value;
        throw new InvalidOperationException("The attributed keyed factory produced no successful output.");
    }
}
