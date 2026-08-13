using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipePipelineLivenessHealthCheck(
    PipelineKey key,
    ISmartPipeRunObservationSource source,
    IOptionsMonitor<SmartPipeLivenessOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private readonly SmartPipeLivenessPolicy _policy = new();

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var snapshot = SmartPipeLivenessOptionsSnapshot.From(options.Get(context.Registration.Name));
            var observation = source.Capture(key);
            if (observation.PipelineKey != key)
                throw new InvalidOperationException("Observation key does not match the requested pipeline key.");
            var evaluation = _policy.Evaluate(
                observation,
                snapshot,
                timeProvider.GetUtcNow().ToUniversalTime(),
                context.Registration.FailureStatus);
            return Task.FromResult(new HealthCheckResult(
                evaluation.Status,
                evaluation.Description,
                exception: null,
                evaluation.Data));
        }
        catch (OperationCanceledException error) when (
            cancellationToken.IsCancellationRequested
            && error.CancellationToken == cancellationToken)
        {
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);
        }
        catch
        {
            return Task.FromResult(Sanitized(context, key, "liveness"));
        }
    }

    private static HealthCheckResult Sanitized(
        HealthCheckContext context,
        PipelineKey pipelineKey,
        string kind) => new(
            context.Registration.FailureStatus,
            $"SmartPipe {kind} evaluation failed for pipeline '{pipelineKey.Value}'.",
            exception: null,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["smartpipe.pipeline_key"] = pipelineKey.Value,
                ["smartpipe.check_kind"] = kind,
            });
}

internal sealed class SmartPipePipelineReadinessHealthCheck(
    PipelineKey key,
    ISmartPipeRunObservationSource source,
    IOptionsMonitor<SmartPipeReadinessOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private readonly SmartPipeReadinessPolicy _policy = new();

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var snapshot = SmartPipeReadinessOptionsSnapshot.From(options.Get(context.Registration.Name));
            var observation = source.Capture(key);
            if (observation.PipelineKey != key)
                throw new InvalidOperationException("Observation key does not match the requested pipeline key.");
            var evaluation = _policy.Evaluate(
                observation,
                snapshot,
                timeProvider.GetUtcNow().ToUniversalTime(),
                context.Registration.FailureStatus);
            return Task.FromResult(new HealthCheckResult(
                evaluation.Status,
                evaluation.Description,
                exception: null,
                evaluation.Data));
        }
        catch (OperationCanceledException error) when (
            cancellationToken.IsCancellationRequested
            && error.CancellationToken == cancellationToken)
        {
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);
        }
        catch
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                $"SmartPipe readiness evaluation failed for pipeline '{key.Value}'.",
                exception: null,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["smartpipe.pipeline_key"] = key.Value,
                    ["smartpipe.check_kind"] = "readiness",
                }));
        }
    }
}
