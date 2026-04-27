using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Health check that reports SmartPipe pipeline status:
/// Circuit Breaker state, Backpressure level, Queue size.
/// </summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public class SmartPipeHealthCheck<TInput, TOutput> : IHealthCheck
{
    private readonly SmartPipeChannel<TInput, TOutput> _pipeline;

    public SmartPipeHealthCheck(SmartPipeChannel<TInput, TOutput> pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var metrics = _pipeline.Metrics;
        bool isHealthy = true;
        var data = new Dictionary<string, object>
        {
            ["ItemsProcessed"] = metrics.ItemsProcessed,
            ["ItemsFailed"] = metrics.ItemsFailed,
            ["QueueSize"] = metrics.QueueSize,
            ["AvgLatencyMs"] = metrics.AvgLatencyMs,
            ["SmoothThroughput"] = metrics.SmoothThroughput
        };

        // Check failure rate
        if (metrics.ItemsProcessed + metrics.ItemsFailed > 100)
        {
            double failureRate = (double)metrics.ItemsFailed / (metrics.ItemsProcessed + metrics.ItemsFailed);
            data["FailureRate"] = failureRate;
            if (failureRate > 0.5)
                isHealthy = false;
        }

        // Check queue size
        if (metrics.QueueSize > 1000)
        {
            isHealthy = false;
            data["Warning"] = "Queue size exceeds 1000";
        }

        var status = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded;
        return Task.FromResult(new HealthCheckResult(status, 
            isHealthy ? "Pipeline is healthy" : "Pipeline is degraded", 
            data: data));
    }
}
