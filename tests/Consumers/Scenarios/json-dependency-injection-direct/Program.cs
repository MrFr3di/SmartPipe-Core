using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Json;

var inputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-json-di-{Guid.NewGuid():N}-input.json");
var outputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-json-di-{Guid.NewGuid():N}-output.jsonl");
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
        .TransformJson(
            new PipelineStageKey("json-round-trip"),
            ConsumerJsonContext.Default.ConsumerModel,
            ConsumerJsonContext.Default.ConsumerModel)
        .ToJsonFile(
            outputPath,
            ConsumerJsonContext.Default.ConsumerModel,
            ConsumerJsonContext.Default.ListConsumerModel,
            new JsonFileSinkOptions
            {
                Format = JsonFileFormat.BatchJsonLines,
                OpenMode = JsonFileOpenMode.Create,
                FlushInterval = 1,
            });

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
    await run.Completion;
    var output = await File.ReadAllTextAsync(outputPath);
    if (!output.Contains("13", StringComparison.Ordinal)) return 1;
}
finally
{
    File.Delete(inputPath);
    File.Delete(outputPath);
}

Console.WriteLine("CONSUMER_OK json-dependency-injection-direct");
return 0;

internal sealed record ConsumerModel(int Value);
[JsonSerializable(typeof(ConsumerModel))]
[JsonSerializable(typeof(List<ConsumerModel>))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
