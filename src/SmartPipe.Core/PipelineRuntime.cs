#nullable enable

namespace SmartPipe.Core;

/// <summary>Represents the single-use runtime boundary for a compiled pipeline plan.</summary>
/// <remarks>
/// This type is introduced in SmartPipe 1.1.0 as the ownership boundary for channels, workers,
/// cancellation, retry scheduling, output, observers, and component disposal. The initial
/// implementation is intentionally minimal while existing <see cref="SmartPipeChannel{TInput,TOutput}"/>
/// behavior is migrated behind this boundary.
/// </remarks>
public sealed class PipelineRuntime
{
    /// <summary>Creates a runtime for a compiled execution plan.</summary>
    /// <param name="executionPlan">Execution plan to run.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="executionPlan"/> is null.</exception>
    public PipelineRuntime(PipelineExecutionPlan executionPlan)
    {
        ExecutionPlan = executionPlan ?? throw new ArgumentNullException(nameof(executionPlan));
        ExecutionPlan.Definition.MarkRuntimeCreated();
        RunId = Guid.NewGuid().ToString("N");
    }

    /// <summary>Gets the execution plan owned by this runtime.</summary>
    public PipelineExecutionPlan ExecutionPlan { get; }

    /// <summary>Gets the runtime run identifier.</summary>
    public string RunId { get; }
}
