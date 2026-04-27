using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>Liveness check: is the pipeline alive (not hung)?</summary>
public class SmartPipeLivenessCheck<TIn, TOut> : IHealthCheck
{
    private readonly SmartPipeChannel<TIn, TOut> _pipe;
    public SmartPipeLivenessCheck(SmartPipeChannel<TIn, TOut> p) => _pipe = p;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        var ok = !_pipe.IsPaused;
        return Task.FromResult(ok
            ? HealthCheckResult.Healthy("Pipeline is alive")
            : HealthCheckResult.Unhealthy("Pipeline is paused"));
    }
}

/// <summary>Readiness check: can the pipeline accept data?</summary>
public class SmartPipeReadinessCheck<TIn, TOut> : IHealthCheck
{
    private readonly SmartPipeChannel<TIn, TOut> _pipe;
    public SmartPipeReadinessCheck(SmartPipeChannel<TIn, TOut> p) => _pipe = p;

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
