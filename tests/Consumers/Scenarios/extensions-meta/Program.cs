using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using Mapster;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

_ = typeof(PipelineBuilder);
_ = typeof(JsonTransform<string, string>);

var composite = new CompositeTransform<int>(new FilterTransform<int>(static value => value > 0));
await composite.InitializeAsync();
_ = new FilterTransform<int>(static value => value > 0)
    & !new FilterTransform<int>(static value => value < 100);
_ = new ValidationTransform<int>().Require(static value => value > 0, "positive required");
_ = new LoggerSink<int>(NullLogger<LoggerSink<int>>.Instance);

var forwarded = typeof(DapperSelector<>).Assembly.GetForwardedTypes();
Type[] expectedForwarded =
[
    typeof(ChannelMerge), typeof(CompositeTransform<>), typeof(FilterTransform<>),
    typeof(LoggerSink<>), typeof(ValidationTransform<>),
];
if (expectedForwarded.Except(forwarded).Any())
    throw new InvalidOperationException("SP220-07 facade reflection identity failed.");

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
