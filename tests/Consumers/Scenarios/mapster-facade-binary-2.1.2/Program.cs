using Mapster;
using SmartPipe.ConsumerScenarios;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

var config = new TypeAdapterConfig();
config.NewConfig<Person, PersonDto>()
    .Map(destination => destination.Label, source => source.Name.ToUpperInvariant());

await using var transform = new MapsterTransform<Person, PersonDto>(config);
await transform.InitializeAsync();

var result = await transform.TransformAsync(ProcessingEnvelope<Person>.Create(new Person { Name = "Ada" }));
if (!result.IsSuccess)
    throw new InvalidOperationException($"Legacy Mapster transform failed: {result.Error?.Message}");
if (result.Value?.Label != "ADA")
    throw new InvalidOperationException($"Unexpected legacy Mapster mapping: '{result.Value?.Label}'.");

await using var defaultTransform = new MapsterTransform<Person, PersonDto>();
await defaultTransform.InitializeAsync();
var defaultResult = await defaultTransform.TransformAsync(ProcessingEnvelope<Person>.Create(new Person { Name = "Grace" }));
if (!defaultResult.IsSuccess || defaultResult.Value?.Name != "Grace")
    throw new InvalidOperationException($"Unexpected default Mapster mapping: '{defaultResult.Value?.Name}'.");

Console.WriteLine("CONSUMER_OK mapster-facade-binary-2.1.2");
return 0;

namespace SmartPipe.ConsumerScenarios
{
    internal sealed class Person
    {
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class PersonDto
    {
        public string Name { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;
    }
}
