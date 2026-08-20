using SmartPipe.Core;

namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Configures an aggregate readiness check.</summary>
public sealed class SmartPipeAggregateReadinessOptions
{
    /// <summary>Gets or sets the maximum number of problem keys emitted.</summary>
    public int MaximumReportedProblemKeys { get; set; } = 10;

    /// <summary>Gets explicitly included exact pipeline keys.</summary>
    public IList<PipelineKey> IncludedPipelines { get; } = new List<PipelineKey>();

    /// <summary>Gets or sets whether all registered pipelines are included.</summary>
    public bool IncludeAllRegisteredPipelines { get; set; } = true;

    /// <summary>Gets the per-pipeline readiness policy.</summary>
    public SmartPipeReadinessOptions Readiness { get; } = new();
}
