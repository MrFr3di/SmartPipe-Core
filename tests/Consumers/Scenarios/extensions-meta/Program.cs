using Mapster;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

_ = typeof(PipelineBuilder);
_ = typeof(JsonTransform<string, string>);

var defaultTransform = new MapsterTransform<DefaultSource, DefaultDestination>();
var defaultResult = await defaultTransform.TransformAsync(
    ProcessingEnvelope<DefaultSource>.Create(new DefaultSource { Name = "Alice", Age = 25 }));
if (!defaultResult.IsSuccess || defaultResult.Value?.Name != "Alice" || defaultResult.Value.Age != 25)
{
    throw new InvalidOperationException("Default Mapster facade mapping failed.");
}

var config = new TypeAdapterConfig();
config.NewConfig<ConfiguredSource, ConfiguredDestination>()
    .Map(destination => destination.DisplayName, source => source.Name);
var configuredTransform = new MapsterTransform<ConfiguredSource, ConfiguredDestination>(config);
var configuredResult = await configuredTransform.TransformAsync(
    ProcessingEnvelope<ConfiguredSource>.Create(new ConfiguredSource { Name = "Bob" }));
if (!configuredResult.IsSuccess || configuredResult.Value?.DisplayName != "Bob")
{
    throw new InvalidOperationException("Configured Mapster facade mapping failed.");
}

Console.WriteLine("CONSUMER_OK extensions-meta");

internal sealed class DefaultSource
{
    public required string Name { get; init; }
    public int Age { get; init; }
}

internal sealed class DefaultDestination
{
    public string? Name { get; init; }
    public int Age { get; init; }
}

internal sealed class ConfiguredSource
{
    public required string Name { get; init; }
}

internal sealed class ConfiguredDestination
{
    public string? DisplayName { get; init; }
}
