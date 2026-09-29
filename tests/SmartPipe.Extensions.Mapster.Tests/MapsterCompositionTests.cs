#nullable enable

using FluentAssertions;
using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster.Tests;

public sealed class MapsterCompositionTests
{
    [Fact]
    public async Task Transform_InvokesConfigureExactlyOnceDuringComposition()
    {
        var invocations = 0;
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
        {
            invocations++;
            config.NewConfig<Source, Destination>().Map(destination => destination.Value, source => source.N + 1);
        });

        invocations.Should().Be(1);

        await MapsterHarness.RunAsync(component, [new Source { N = 1 }, new Source { N = 2 }]);

        invocations.Should().Be(1);
    }

    [Fact]
    public async Task Transform_CompilesTheRootPairOnceAtCompositionAndNeverPerRun()
    {
        var compiles = 0;
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
        {
            var originalCompiler = config.Compiler;
            config.Compiler = lambda =>
            {
                compiles++;
                return originalCompiler(lambda);
            };
            config.NewConfig<Source, Destination>().Map(destination => destination.Value, source => source.N);
        });

        compiles.Should().Be(1);

        var first = await MapsterHarness.RunAsync(component, [new Source { N = 1 }]);
        var second = await MapsterHarness.RunAsync(component, [new Source { N = 2 }]);

        first.Should().ContainSingle().Which.Value.Should().Be(1);
        second.Should().ContainSingle().Which.Value.Should().Be(2);
        compiles.Should().Be(1);
    }

    [Fact]
    public void Transform_ThrowingConfigureIsACompositionFailure()
    {
        var act = () => MapsterPipelineComponents.Transform<Source, Destination>(
            _ => throw new InvalidOperationException("mapster-configure-failure"));

        act.Should().Throw<InvalidOperationException>().WithMessage("mapster-configure-failure");
    }

    [Fact]
    public async Task Transform_IsolatesRuleReplacementButSharesCapturedClosures()
    {
        var offset = new[] { 7 };
        TypeAdapterConfig? workingConfiguration = null;
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
        {
            workingConfiguration = config;
            config.NewConfig<Source, Destination>().Map(destination => destination.Value, source => source.N + offset[0]);
        });

        workingConfiguration.Should().NotBeNull();
        workingConfiguration!.NewConfig<Source, Destination>()
            .Map(destination => destination.Value, source => source.N * 100);

        var afterRuleReplacement = await MapsterHarness.RunAsync(component, [new Source { N = 1 }]);
        afterRuleReplacement.Should().ContainSingle().Which.Value.Should().Be(8);

        offset[0] = 10;
        var afterClosureMutation = await MapsterHarness.RunAsync(component, [new Source { N = 1 }]);
        afterClosureMutation.Should().ContainSingle().Which.Value.Should().Be(11);
    }

    [Fact]
    public async Task Transform_NeverReadsGlobalSettings()
    {
        TypeAdapterConfig.GlobalSettings.NewConfig<GlobalSource, GlobalDestination>()
            .Map(destination => destination.Value, source => 999);

        var component = MapsterPipelineComponents.Transform<GlobalSource, GlobalDestination>();
        var results = await MapsterHarness.RunAsync(component, [new GlobalSource { N = 1 }]);

        results.Should().ContainSingle().Which.Value.Should().Be(0);
    }

    [Fact]
    public void Transform_UnmappedStrictMemberFailsAtCompositionRatherThanPerItem()
    {
        var act = () => MapsterPipelineComponents.Transform<Source, Destination>(config =>
        {
            config.RequireDestinationMemberSource = true;
            config.NewConfig<Source, Destination>();
        });

        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task Transform_ReusesOneCapturedDelegateAcrossRuns()
    {
        var compiles = 0;
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
        {
            var originalCompiler = config.Compiler;
            config.Compiler = lambda =>
            {
                compiles++;
                return originalCompiler(lambda);
            };
            config.NewConfig<Source, Destination>().Map(destination => destination.Value, source => source.N);
        });

        var runs = await Task.WhenAll(
            MapsterHarness.RunAsync(component, [new Source { N = 3 }]),
            MapsterHarness.RunAsync(component, [new Source { N = 4 }]),
            MapsterHarness.RunAsync(component, [new Source { N = 5 }]));

        runs.SelectMany(result => result).Select(item => item.Value).Should().BeEquivalentTo([3, 4, 5]);
        compiles.Should().Be(1);
    }
}
