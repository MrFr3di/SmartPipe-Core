using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class PipelineDefinitionTests
{
    [Fact]
    public void IsReusable_ShouldBeFalse_WhenSingleUseComponentExists()
    {
        var definition = new PipelineDefinition(
            "pipeline",
            [
                new PipelineComponentRegistration(
                    typeof(string),
                    PipelineComponentLifetime.SingleUse,
                    OwnsResources: true,
                    IsFactoryBased: false
                ),
            ],
            [],
            ComponentOwnershipOptions.Default,
            LineageMode.Minimal
        );

        definition.IsReusable.Should().BeFalse();
    }

    [Fact]
    public void MarkRuntimeCreated_ShouldThrow_OnSecondSingleUseRun()
    {
        var definition = new PipelineDefinition(
            "pipeline",
            [
                new PipelineComponentRegistration(
                    typeof(string),
                    PipelineComponentLifetime.SingleUse,
                    OwnsResources: true,
                    IsFactoryBased: false
                ),
            ],
            [],
            ComponentOwnershipOptions.Default,
            LineageMode.Minimal
        );

        definition.MarkRuntimeCreated();
        var act = definition.MarkRuntimeCreated;

        act.Should().Throw<InvalidOperationException>().WithMessage("*single-use*");
    }

    [Fact]
    public void Compile_ShouldRejectTypeMismatch()
    {
        var definition = new PipelineDefinition(
            "pipeline",
            [],
            [
                new PipelineStageDefinition(
                    "stage-1",
                    "first",
                    typeof(string),
                    typeof(int),
                    StageFailureOptions.Default
                ),
                new PipelineStageDefinition(
                    "stage-2",
                    "second",
                    typeof(string),
                    typeof(string),
                    StageFailureOptions.Default
                ),
            ],
            ComponentOwnershipOptions.Default,
            LineageMode.Minimal
        );

        var act = () => PipelineExecutionPlan.Compile(definition);

        act.Should().Throw<InvalidOperationException>().WithMessage("*expects input type*");
    }
}
