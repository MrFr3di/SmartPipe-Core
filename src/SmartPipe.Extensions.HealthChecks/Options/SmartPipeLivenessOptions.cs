namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Configures a liveness policy for one pipeline.</summary>
public sealed class SmartPipeLivenessOptions
{
    /// <summary>Gets or sets whether the latest runtime fault is a liveness failure.</summary>
    public bool FailOnLatestFault { get; set; } = true;

    /// <summary>Gets or sets whether the latest activation failure is a liveness failure.</summary>
    public bool FailOnActivationFailure { get; set; }

    /// <summary>Gets or sets the maximum number of problem runs represented in bounded output.</summary>
    public int MaximumReportedProblemRuns { get; set; } = 10;
}
