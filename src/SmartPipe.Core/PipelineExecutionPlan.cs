#nullable enable

namespace SmartPipe.Core;

/// <summary>Validated execution plan compiled from a <see cref="PipelineDefinition"/>.</summary>
/// <remarks>
/// The execution plan is responsible for topology and lifetime validation before any runtime
/// component is initialized. SmartPipe introduces this as an explicit boundary for runtime
/// metrics, diagnostics, and graph export.
/// </remarks>
public sealed class PipelineExecutionPlan
{
    private PipelineExecutionPlan(PipelineDefinition definition)
    {
        Definition = definition;
    }

    /// <summary>Gets the source definition.</summary>
    public PipelineDefinition Definition { get; }

    /// <summary>Compiles and validates a pipeline definition.</summary>
    /// <param name="definition">Definition to compile.</param>
    /// <returns>A validated execution plan.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the definition is invalid.</exception>
    public static PipelineExecutionPlan Compile(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateStages(definition);
        return new PipelineExecutionPlan(definition);
    }

    private static void ValidateStages(PipelineDefinition definition)
    {
        var topology = new PipelineStageTopologyEntry[definition.Stages.Count];
        for (var index = 0; index < definition.Stages.Count; index++)
        {
            var stage = definition.Stages[index]
                ?? throw new InvalidOperationException(
                    $"Stage definition at index {index} is null.");
            topology[index] = new(
                stage.StageId,
                stage.StageName,
                stage.InputType,
                stage.OutputType);
        }

        PipelineStageTopologyValidator.Validate(topology);
    }
}
