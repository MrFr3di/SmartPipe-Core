#nullable enable

namespace SmartPipe.Extensions;

/// <summary>Options for typed SmartPipe health checks.</summary>
public sealed class SmartPipeHealthCheckOptions
{
    /// <summary>
    /// Gets the queue utilization threshold that reports degraded health.
    /// </summary>
    public double QueueUtilizationDegradedThreshold { get; init; } = 0.80;

    /// <summary>
    /// Gets the duration after the last processed item that reports degraded health while running.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets a value indicating whether a pipeline with no started run reports degraded health.
    /// </summary>
    public bool TreatNotStartedAsDegraded { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether a running pipeline must report initial activity within the grace period.
    /// </summary>
    public bool RequireInitialActivity { get; init; } = false;

    /// <summary>
    /// Gets the grace period before a running pipeline with no activity reports degraded health.
    /// </summary>
    public TimeSpan InitialActivityGracePeriod { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets the time provider used for health-policy time comparisons.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Validates option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an option value is outside its valid range.</exception>
    public void Validate()
    {
        if (QueueUtilizationDegradedThreshold <= 0 || QueueUtilizationDegradedThreshold > 1)
            throw new ArgumentOutOfRangeException(
                nameof(QueueUtilizationDegradedThreshold),
                QueueUtilizationDegradedThreshold,
                "Queue utilization threshold must be greater than zero and less than or equal to one.");

        if (StaleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(StaleAfter),
                StaleAfter,
                "StaleAfter must be greater than zero.");

        if (InitialActivityGracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(InitialActivityGracePeriod),
                InitialActivityGracePeriod,
                "InitialActivityGracePeriod must be greater than zero.");

        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}
