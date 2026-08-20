namespace SmartPipe.Extensions.HealthChecks;

internal sealed record SmartPipeLivenessOptionsSnapshot(
    bool FailOnLatestFault,
    bool FailOnActivationFailure,
    int MaximumReportedProblemRuns)
{
    internal static SmartPipeLivenessOptionsSnapshot From(SmartPipeLivenessOptions options) =>
        new(options.FailOnLatestFault, options.FailOnActivationFailure, options.MaximumReportedProblemRuns);
}

internal sealed record SmartPipeReadinessOptionsSnapshot(
    SmartPipeReadinessRunRequirement RunRequirement,
    bool FailOnLatestFailure,
    bool RequireInitialActivity,
    TimeSpan InitialActivityGracePeriod,
    TimeSpan? StaleAfter,
    Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus InitialActivityStatus,
    Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus StaleActivityStatus,
    double? QueueUtilizationDegradedThreshold,
    Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus QueuePressureStatus,
    int MaximumReportedProblemRuns)
{
    internal static SmartPipeReadinessOptionsSnapshot From(SmartPipeReadinessOptions options) => new(
        options.RunRequirement,
        options.FailOnLatestFailure,
        options.RequireInitialActivity,
        options.InitialActivityGracePeriod,
        options.StaleAfter,
        options.InitialActivityStatus,
        options.StaleActivityStatus,
        options.QueueUtilizationDegradedThreshold,
        options.QueuePressureStatus,
        options.MaximumReportedProblemRuns);
}
