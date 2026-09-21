#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster.Tests;

public sealed class MapsterBuilderTests
{
    [Fact]
    public void MapWithMapster_InitialBuilderPreservesInputAndStageKey()
    {
        var stageKey = new PipelineStageKey("initial-map");
        var source = PipelineComponent.RuntimeOwned<IPipelineSource<Source>>(
            (context, cancellationToken) => ValueTask.FromResult(
                PipelineSource.FromAsyncEnumerable(Array.Empty<Source>().ToAsyncEnumerable(), "mapster-tests", context.RunId.ToString())));

        var definition = PipelineDefinitionBuilder
            .From(new PipelineKey("mapster-builder-tests"), source)
            .MapWithMapster<Source, Destination>(
                stageKey,
                config => config.NewConfig<Source, Destination>().Map(destination => destination.Value, input => input.N))
            .Build();

        definition.Should().BeOfType<PipelineDefinition<Source, Destination>>();
        definition.Key.Value.Should().Be("mapster-builder-tests");
        definition.Stages.Should().ContainSingle();
        definition.Stages[0].Key.Should().Be(stageKey);
        definition.Stages[0].InputType.Should().Be(typeof(Source));
        definition.Stages[0].OutputType.Should().Be(typeof(Destination));
    }

    [Fact]
    public async Task MapWithMapster_TypedBuilderPreservesPipelineInputAndStageKey()
    {
        var firstStage = new PipelineStageKey("tag");
        var secondStage = new PipelineStageKey("typed-map");
        var source = PipelineComponent.RuntimeOwned<IPipelineSource<Source>>(
            (context, cancellationToken) => ValueTask.FromResult(
                PipelineSource.FromAsyncEnumerable(Emit(), "mapster-tests", context.RunId.ToString())));
        var tagger = PipelineComponent.RuntimeOwned<IPipelineTransformer<Source, TaggedSource>>(
            (context, cancellationToken) => ValueTask.FromResult(
                PipelineTransformer.FromFunc<Source, TaggedSource>(
                    (input, token) => ValueTask.FromResult(new TaggedSource { Person = input, Tag = "tagged" }))));

        var definition = PipelineDefinitionBuilder
            .From(new PipelineKey("mapster-typed-builder-tests"), source)
            .Transform(firstStage, tagger)
            .MapWithMapster<Source, TaggedSource, Destination>(
                secondStage,
                config => config.NewConfig<TaggedSource, Destination>()
                    .Map(destination => destination.Value, input => input.Person.N))
            .Build();

        definition.Should().BeOfType<PipelineDefinition<Source, Destination>>();
        definition.Stages.Select(stage => stage.Key).Should().Equal(firstStage, secondStage);
        definition.Stages[1].InputType.Should().Be(typeof(TaggedSource));
        definition.Stages[1].OutputType.Should().Be(typeof(Destination));

        var results = new List<Destination>();
        await using var run = await definition.StartAsync();
        await foreach (var output in run.Outputs.ReadAllAsync())
        {
            results.Add(output.Result.Value!);
        }

        await run.Completion;
        results.Should().ContainSingle().Which.Value.Should().Be(7);
    }

    [Fact]
    public void MapWithMapster_ThrowingConfigureIsACompositionFailure()
    {
        var source = PipelineComponent.RuntimeOwned<IPipelineSource<Source>>(
            (context, cancellationToken) => ValueTask.FromResult(
                PipelineSource.FromAsyncEnumerable(Array.Empty<Source>().ToAsyncEnumerable(), "mapster-tests", context.RunId.ToString())));
        var builder = PipelineDefinitionBuilder.From(new PipelineKey("mapster-builder-failure"), source);

        var act = () => builder.MapWithMapster<Source, Destination>(
            new PipelineStageKey("map"),
            _ => throw new InvalidOperationException("mapster-builder-configure-failure"));

        act.Should().Throw<InvalidOperationException>().WithMessage("mapster-builder-configure-failure");
    }

    private static async IAsyncEnumerable<Source> Emit()
    {
        await Task.Yield();
        yield return new Source { N = 7 };
    }
}
