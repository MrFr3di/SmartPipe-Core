#nullable enable

namespace SmartPipe.Core;

/// <summary>Represents the single-use runtime boundary for a compiled pipeline plan.</summary>
/// <remarks>
/// This type is the ownership boundary for channels, workers, cancellation, retry scheduling,
/// output, observers, and component disposal.
/// </remarks>
public sealed class PipelineRuntime
{
    /// <summary>Creates a runtime for a compiled execution plan.</summary>
    /// <param name="executionPlan">Execution plan to run.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="executionPlan"/> is null.</exception>
    public PipelineRuntime(PipelineExecutionPlan executionPlan)
        : this(executionPlan, Guid.NewGuid().ToString("N"))
    {
    }

    internal PipelineRuntime(PipelineExecutionPlan executionPlan, string runId)
    {
        ExecutionPlan = executionPlan ?? throw new ArgumentNullException(nameof(executionPlan));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ExecutionPlan.Definition.MarkRuntimeCreated();
        RunId = runId;
        Options = ExecutionPlan.Definition.RuntimeOptions;
    }

    /// <summary>Gets the execution plan owned by this runtime.</summary>
    public PipelineExecutionPlan ExecutionPlan { get; }

    /// <summary>Gets the runtime run identifier.</summary>
    public string RunId { get; }

    /// <summary>Gets runtime execution options.</summary>
    public PipelineRuntimeOptions Options { get; }
}
