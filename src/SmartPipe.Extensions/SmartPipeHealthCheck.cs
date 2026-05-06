using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Liveness health check for <see cref="SmartPipeChannel{TIn, TOut}"/>.
/// Reports healthy when the pipeline is not paused (alive and responsive).
/// </summary>
/// <typeparam name="TIn">Pipeline input type.</typeparam>
/// <typeparam name="TOut">Pipeline output type.</typeparam>
public class SmartPipeLivenessCheck<TIn, TOut> : IHealthCheck
{
    private readonly SmartPipeChannel<TIn, TOut> _pipe;

    /// <summary>
    /// Initializes a new instance of <see cref="SmartPipeLivenessCheck{TIn, TOut}"/>.
    /// </summary>
    /// <param name="p">The pipeline to monitor.</param>
    public SmartPipeLivenessCheck(SmartPipeChannel<TIn, TOut> p) => _pipe = p;

    /// <summary>
    /// Checks if the pipeline is alive (not paused).
    /// </summary>
    /// <param name="ctx">The health check context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A healthy result if the pipeline is running; otherwise unhealthy.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        var ok = !_pipe.IsPaused;
        return Task.FromResult(ok
            ? HealthCheckResult.Healthy("Pipeline is alive")
            : HealthCheckResult.Unhealthy("Pipeline is paused"));
    }
}

/// <summary>
/// Readiness health check for <see cref="SmartPipeChannel{TIn, TOut}"/>.
/// Reports ready when the pipeline can accept data (queue size and failure metrics within thresholds).
/// </summary>
/// <typeparam name="TIn">Pipeline input type.</typeparam>
/// <typeparam name="TOut">Pipeline output type.</typeparam>
public class SmartPipeReadinessCheck<TIn, TOut> : IHealthCheck
{
    private readonly SmartPipeChannel<TIn, TOut> _pipe;

    /// <summary>
    /// Initializes a new instance of <see cref="SmartPipeReadinessCheck{TIn, TOut}"/>.
    /// </summary>
    /// <param name="p">The pipeline to monitor.</param>
    public SmartPipeReadinessCheck(SmartPipeChannel<TIn, TOut> p) => _pipe = p;

    /// <summary>
    /// Checks if the pipeline is ready to accept data based on queue size and failure metrics.
    /// </summary>
    /// <param name="ctx">The health check context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Healthy if ready; Degraded if queue size exceeds 1000 or failures detected.
    /// </returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        var m = _pipe.Metrics;
        var data = new Dictionary<string, object>
        {
            ["QueueSize"] = m.QueueSize,
            ["Failures"] = m.ItemsFailed,
            ["AvgLatencyMs"] = m.AvgLatencyMs
        };

        if (m.QueueSize > 1000)
            return Task.FromResult(HealthCheckResult.Degraded("Queue too large", data: data));

        if (m.ItemsFailed > 0)
            return Task.FromResult(HealthCheckResult.Degraded("Failures detected", data: data));

        return Task.FromResult(HealthCheckResult.Healthy("Ready", data));
    }
}
