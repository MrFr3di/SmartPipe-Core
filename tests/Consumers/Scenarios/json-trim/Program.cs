using System.Text.Json.Serialization;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Json;

var inputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-json-trim-{Guid.NewGuid():N}-input.json");
var outputPath = Path.Combine(Path.GetTempPath(), $"smartpipe-json-trim-{Guid.NewGuid():N}-output.jsonl");
try
{
    await File.WriteAllTextAsync(inputPath, "[{\"Value\":11}]\n");
    var definition = JsonPipelineDefinitionBuilder
        .FromJsonFile(
            new PipelineKey("json-trim"),
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

    await using var run = await definition.StartAsync();
    await run.Completion;
    var output = await File.ReadAllTextAsync(outputPath);
    if (!output.Contains("11", StringComparison.Ordinal)) return 1;
}
finally
{
    File.Delete(inputPath);
    File.Delete(outputPath);
}

Console.WriteLine("CONSUMER_OK json-trim");
return 0;

internal sealed record ConsumerModel(int Value);
[JsonSerializable(typeof(ConsumerModel))]
[JsonSerializable(typeof(List<ConsumerModel>))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
