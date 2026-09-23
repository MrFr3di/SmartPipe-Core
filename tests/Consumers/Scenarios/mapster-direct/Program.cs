using SmartPipe.ConsumerScenarios;
using SmartPipe.Core;
using SmartPipe.Extensions.Mapster;

var initialDefinition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("mapster-direct"),
        PipelineComponent.RuntimeOwned<IPipelineSource<Person>>(
            static (context, cancellationToken) => ValueTask.FromResult(
                PipelineSource.FromAsyncEnumerable(People(), "mapster-direct", context.RunId.ToString()))))
    .MapWithMapster<Person, PersonDto>(
        new PipelineStageKey("map"),
        static config => config
            .NewConfig<Person, PersonDto>()
            .Map(destination => destination.Label, source => source.Name.ToUpperInvariant())
            .Map(destination => destination.City, source => source.Address.City))
    .Build();

var initialResults = await RunAsync(initialDefinition);
if (initialResults.Count != 2)
    throw new InvalidOperationException($"Unexpected initial result count: {initialResults.Count}.");
if (initialResults[0].Label != "ADA" || initialResults[0].City != "London")
    throw new InvalidOperationException($"Unexpected configured mapping: '{initialResults[0].Label}'/'{initialResults[0].City}'.");
if (initialResults[1].Label != "GRACE" || initialResults[1].City != "Arlington")
    throw new InvalidOperationException($"Unexpected configured mapping: '{initialResults[1].Label}'/'{initialResults[1].City}'.");

var typedDefinition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("mapster-direct-typed"),
        PipelineComponent.RuntimeOwned<IPipelineSource<Person>>(
            static (context, cancellationToken) => ValueTask.FromResult(
                PipelineSource.FromAsyncEnumerable(People(), "mapster-direct-typed", context.RunId.ToString()))))
    .Transform(
        new PipelineStageKey("tag"),
        PipelineComponent.RuntimeOwned<IPipelineTransformer<Person, TaggedPerson>>(
            static (context, cancellationToken) => ValueTask.FromResult(
                PipelineTransformer.FromFunc<Person, TaggedPerson>(
                    static (person, token) => ValueTask.FromResult(new TaggedPerson(person, "tagged"))))))
    .MapWithMapster<Person, TaggedPerson, PersonDto>(
        new PipelineStageKey("map-typed"),
        static config => config
            .NewConfig<TaggedPerson, PersonDto>()
            .Map(destination => destination.Label, source => source.Tag + ":" + source.Person.Name))
    .Build();

var typedResults = await RunAsync(typedDefinition);
if (typedResults.Count != 2)
    throw new InvalidOperationException($"Unexpected typed result count: {typedResults.Count}.");
if (typedResults[0].Label != "tagged:Ada" || typedResults[1].Label != "tagged:Grace")
    throw new InvalidOperationException($"Unexpected typed mapping: '{typedResults[0].Label}'/'{typedResults[1].Label}'.");

Console.WriteLine("CONSUMER_OK mapster-direct");
return 0;

static async IAsyncEnumerable<Person> People()
{
    yield return new Person("Ada", new Address("London"));
    yield return new Person("Grace", new Address("Arlington"));
}

static async Task<List<PersonDto>> RunAsync(PipelineDefinition<Person, PersonDto> definition)
{
    var results = new List<PersonDto>();
    await using var run = await definition.StartAsync();
    await foreach (var output in run.Outputs.ReadAllAsync())
    {
        var value = output.Result.Value;
        if (value is null)
            throw new InvalidOperationException("Mapster transform produced no value.");
        results.Add(value);
    }

    await run.Completion;
    return results;
}

namespace SmartPipe.ConsumerScenarios
{
    internal sealed record Person(string Name, Address Address);

    internal sealed record Address(string City);

    internal sealed record TaggedPerson(Person Person, string Tag);

    internal sealed record PersonDto
    {
        public string Label { get; init; } = string.Empty;

        public string City { get; init; } = string.Empty;
    }
}
