using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;
using SmartPipe.Extensions.Mapster;

internal static class Program
{
    // The annotated Mapster composition path is compiled and analyzed here, but never executed: this
    // consumer records the boundary instead of pretending that Mapster runtime mapping is trim-safe.
    internal static readonly Action AnnotatedBoundaryReference = ComposeAnnotatedMapsterBoundary;

    private static async Task<int> Main()
    {
        // Positive route: a hand-written mapper through Core's FromFunc publishes and runs under
        // TrimMode=link without any suppression.
        var definition = PipelineDefinitionBuilder
            .From(
                new PipelineKey("mapster-trim-diagnostic"),
                PipelineComponent.RuntimeOwned<IPipelineSource<Person>>(
                    static (context, cancellationToken) => ValueTask.FromResult(
                        PipelineSource.FromAsyncEnumerable(People(), "mapster-trim-diagnostic", context.RunId.ToString()))))
            .Transform(
                new PipelineStageKey("map"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<Person, PersonDto>>(
                    static (context, cancellationToken) => ValueTask.FromResult(
                        PipelineTransformer.FromFunc<Person, PersonDto>(
                            static (person, token) => ValueTask.FromResult(new PersonDto { Label = person.Name.ToUpperInvariant() })))))
            .Build();

        var count = 0;
        await using (var run = await definition.StartAsync())
        {
            await foreach (var output in run.Outputs.ReadAllAsync())
            {
                if (output.Result.Value?.Label != "ADA")
                    throw new InvalidOperationException("Trimmed hand-written mapping produced an unexpected value.");
                count++;
            }

            await run.Completion;
        }

        if (count != 1)
            throw new InvalidOperationException($"Unexpected trimmed result count: {count}.");

        Console.WriteLine("CONSUMER_OK mapster-trim-diagnostic");
        return 0;
    }

    // Mapster runtime mapping uses reflection metadata and runtime expression compilation, and composing it
    // under TrimMode=link fails. The suppression documents the annotated boundary; the trimming-safe route
    // is the one executed above.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This diagnostic consumer documents the Mapster reflection boundary.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "This diagnostic consumer documents the Mapster dynamic-code boundary.")]
    private static void ComposeAnnotatedMapsterBoundary() =>
        _ = MapsterPipelineComponents.Transform<Person, PersonDto>(
            static config => config.NewConfig<Person, PersonDto>().Map(destination => destination.Label, source => source.Name));

    private static async IAsyncEnumerable<Person> People()
    {
        await Task.Yield();
        yield return new Person("Ada");
    }

    private sealed record Person(string Name);

    private sealed record PersonDto
    {
        public string Label { get; init; } = string.Empty;
    }
}
