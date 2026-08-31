using JsonDependencyInjectionConsumer;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Json;

var inputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-json-di-{Guid.NewGuid():N}-input.json");
try
{
    await File.WriteAllTextAsync(inputPath, "[{\"Value\":13}]\n");
    var key = new PipelineKey("json-dependency-injection-direct");
    var definition = JsonPipelineDefinitionBuilder
        .FromJsonFile(
            key,
            inputPath,
            ConsumerJsonContext.Default.ConsumerModel,
            ConsumerJsonContext.Default.ListConsumerModel,
            new JsonFileSourceOptions { Format = JsonFileFormat.Array })
        .Build();

    var services = new ServiceCollection();
    services.AddSmartPipe().AddPipeline(definition);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    });
    var factory = provider
        .GetRequiredService<ISmartPipeFactoryProvider>()
        .GetFactory<ConsumerModel, ConsumerModel>(key);
    await using var run = await factory.StartAsync();
    var output = await run.Outputs.ReadAsync();
    await run.Completion;
    if (!output.Result.IsSuccess || output.Result.Value?.Value != 13 || run.Outputs.TryRead(out _)) return 1;
}
finally
{
    File.Delete(inputPath);
}

Console.WriteLine("CONSUMER_OK json-dependency-injection-direct");
return 0;

namespace JsonDependencyInjectionConsumer
{
    internal sealed record ConsumerModel(int Value);

    [JsonSerializable(typeof(ConsumerModel))]
    [JsonSerializable(typeof(List<ConsumerModel>))]
    internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
}
