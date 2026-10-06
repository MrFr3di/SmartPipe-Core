using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class PipelineDefinitionTests
{
    [Fact]
    public void Constructor_ShouldDefensivelyCopyCompatibilityCollections()
    {
        var originalComponent = new PipelineComponentRegistration(
            typeof(string),
            PipelineComponentLifetime.SingleUse,
            OwnsResources: true,
            IsFactoryBased: false);
        var originalStage = new PipelineStageDefinition(
            "stage-1",
            "first",
            typeof(string),
            typeof(int),
            StageFailureOptions.Default);
        var components = new[] { originalComponent };
        var stages = new[] { originalStage };

        var definition = new PipelineDefinition("pipeline", components, stages);
        components[0] = originalComponent with { ComponentType = typeof(int) };
        stages[0] = originalStage with { StageId = "mutated" };

        definition.Components.Should().BeOfType<System.Collections.ObjectModel.ReadOnlyCollection<PipelineComponentRegistration>>();
        definition.Stages.Should().BeOfType<System.Collections.ObjectModel.ReadOnlyCollection<PipelineStageDefinition>>();
        definition.Components.Should().ContainSingle().Which.Should().BeSameAs(originalComponent);
        definition.Stages.Should().ContainSingle().Which.Should().BeSameAs(originalStage);
    }

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

    [Fact]
    public void Compile_ShouldRejectDuplicateStageIds()
    {
        var definition = new PipelineDefinition(
            "pipeline",
            stages:
            [
                new PipelineStageDefinition(
                    "stage-1",
                    "first",
                    typeof(string),
                    typeof(int),
                    StageFailureOptions.Default),
                new PipelineStageDefinition(
                    "stage-1",
                    "second",
                    typeof(int),
                    typeof(long),
                    StageFailureOptions.Default),
            ]);

        var act = () => PipelineExecutionPlan.Compile(definition);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate stage ID 'stage-1' at indexes 0 and 1*");
    }
}
