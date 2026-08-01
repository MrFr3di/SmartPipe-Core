using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

var key = new PipelineKey("dependency-injection-direct");
var services = new ServiceCollection();
services.AddScoped<OneSource>();
services.AddSmartPipe().AddPipeline(CreateDefinition(key));
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
var factory = provider.GetRequiredService<ISmartPipeFactoryProvider>().GetFactory<int, int>(key);
await using var run = await factory.StartAsync();
var value = await ReadSingleAsync(run);
if (value != 1 || run.PipelineKey != key || run.RunId == Guid.Empty || run.InputCapacity <= 0 || run.OutputCapacity <= 0)
    return 1;

Console.WriteLine("CONSUMER_OK dependency-injection-direct");
return 0;

static PipelineDefinition<int, int> CreateDefinition(PipelineKey key) => PipelineDefinitionBuilder
    .From(
        key,
        PipelineComponent.ScopeOwned<IPipelineSource<int>>(
            static (context, _) => ValueTask.FromResult<IPipelineSource<int>>(
                context.Services!.GetRequiredService<OneSource>())))
    .Build();

static async Task<int> ReadSingleAsync(PipelineRun<int> run)
{
    await foreach (var output in run.Outputs.ReadAllAsync())
        if (output.Result.IsSuccess)
            return output.Result.Value;
    throw new InvalidOperationException("The DI pipeline produced no successful output.");
}

internal sealed class OneSource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return ProcessingEnvelope<int>.Create(1);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
