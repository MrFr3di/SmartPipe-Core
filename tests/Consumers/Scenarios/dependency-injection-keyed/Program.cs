using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

var firstKey = new PipelineKey("keyed-first");
var secondKey = new PipelineKey("keyed-second");
var services = new ServiceCollection();
services.AddSmartPipe()
    .AddPipeline(CreateDefinition(firstKey, 1));
services.AddSmartPipe()
    .AddPipeline(CreateDefinition(secondKey, 2));
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
var first = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(firstKey.Value);
var second = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(secondKey.Value);
if (await RunAsync(first) != 1 || await RunAsync(second) != 2)
    return 1;

Console.WriteLine("CONSUMER_OK dependency-injection-keyed");
return 0;

static PipelineDefinition<int, int> CreateDefinition(PipelineKey key, int value) => PipelineDefinitionBuilder
    .From(
        key,
        PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
            (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                PipelineSource.FromAsyncEnumerable(Values(value)))))
    .Build();

static async IAsyncEnumerable<int> Values(int value)
{
    yield return value;
    await Task.CompletedTask;
}

static async Task<int> RunAsync(ISmartPipeRunFactory<int, int> factory)
{
    await using var run = await factory.StartAsync();
    await foreach (var output in run.Outputs.ReadAllAsync())
        if (output.Result.IsSuccess)
            return output.Result.Value;
    throw new InvalidOperationException("The keyed DI pipeline produced no successful output.");
}
