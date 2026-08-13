using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Configures a readiness policy for one pipeline.</summary>
public sealed class SmartPipeReadinessOptions
{
    /// <summary>Gets or sets required runtime evidence.</summary>
    public SmartPipeReadinessRunRequirement RunRequirement { get; set; } =
        SmartPipeReadinessRunRequirement.ActiveRunRequired;

    /// <summary>Gets or sets whether the latest failure fails registration-only readiness.</summary>
    public bool FailOnLatestFailure { get; set; } = true;

    /// <summary>Gets or sets whether a running pipeline must report initial activity.</summary>
    public bool RequireInitialActivity { get; set; }

    /// <summary>Gets or sets the grace period for initial activity.</summary>
    public TimeSpan InitialActivityGracePeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the maximum age of the latest activity.</summary>
    public TimeSpan? StaleAfter { get; set; }

    /// <summary>Gets or sets status used when initial activity is missing after grace.</summary>
    public HealthStatus InitialActivityStatus { get; set; } = HealthStatus.Unhealthy;

    /// <summary>Gets or sets status used for stale activity.</summary>
    public HealthStatus StaleActivityStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>Gets or sets the per-run input/output queue utilization threshold.</summary>
    public double? QueueUtilizationDegradedThreshold { get; set; }

    /// <summary>Gets or sets status used for queue pressure.</summary>
    public HealthStatus QueuePressureStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>Gets or sets the maximum number of problem runs represented in bounded output.</summary>
    public int MaximumReportedProblemRuns { get; set; } = 10;
}
