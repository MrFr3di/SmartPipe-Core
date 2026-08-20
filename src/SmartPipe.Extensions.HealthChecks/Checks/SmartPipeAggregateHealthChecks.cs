using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipeAggregateLivenessHealthCheck(
    ISmartPipeRegistry registry,
    ISmartPipeRunObservationSource source,
    IOptionsMonitor<SmartPipeAggregateLivenessOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return SmartPipeAggregateHealthEvaluator.EvaluateAsync(
                context,
                cancellationToken,
                registry,
                source,
                timeProvider,
                options.Get(context.Registration.Name),
                static configured => configured.IncludeAllRegisteredPipelines,
                static configured => configured.IncludedPipelines,
                static configured => configured.MaximumReportedProblemKeys,
                static _ => new SmartPipeLivenessPolicy(),
                static configured => SmartPipeLivenessOptionsSnapshot.From(configured.Liveness),
                "liveness");
        }
        catch (OperationCanceledException error) when (
            cancellationToken.IsCancellationRequested
            && error.CancellationToken == cancellationToken)
        {
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);
        }
        catch
        {
            return Task.FromResult(SmartPipeAggregateHealthEvaluator.Sanitized(context, "liveness"));
        }
    }
}

internal sealed class SmartPipeAggregateReadinessHealthCheck(
    ISmartPipeRegistry registry,
    ISmartPipeRunObservationSource source,
    IOptionsMonitor<SmartPipeAggregateReadinessOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return SmartPipeAggregateHealthEvaluator.EvaluateAsync(
                context,
                cancellationToken,
                registry,
                source,
                timeProvider,
                options.Get(context.Registration.Name),
                static configured => configured.IncludeAllRegisteredPipelines,
                static configured => configured.IncludedPipelines,
                static configured => configured.MaximumReportedProblemKeys,
                static _ => new SmartPipeReadinessPolicy(),
                static configured => SmartPipeReadinessOptionsSnapshot.From(configured.Readiness),
                "readiness");
        }
        catch (OperationCanceledException error) when (
            cancellationToken.IsCancellationRequested
            && error.CancellationToken == cancellationToken)
        {
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);
        }
        catch
        {
            return Task.FromResult(SmartPipeAggregateHealthEvaluator.Sanitized(context, "readiness"));
        }
    }
}

internal static class SmartPipeAggregateHealthEvaluator
{
    internal static Task<HealthCheckResult> EvaluateAsync<TAggregate, TPolicy, TOptions>(
        HealthCheckContext context,
        CancellationToken cancellationToken,
        ISmartPipeRegistry registry,
        ISmartPipeRunObservationSource source,
        TimeProvider timeProvider,
        TAggregate aggregate,
        Func<TAggregate, bool> includeAll,
        Func<TAggregate, IList<PipelineKey>> included,
        Func<TAggregate, int> maximumProblems,
        Func<TAggregate, TPolicy> createPolicy,
        Func<TAggregate, TOptions> snapshotOptions,
        string kind)
        where TPolicy : ISmartPipeHealthPolicy<TOptions>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var includeAllSnapshot = includeAll(aggregate);
            var includedSnapshot = included(aggregate).ToArray();
            var maximum = maximumProblems(aggregate);
            var policy = createPolicy(aggregate);
            var policyOptions = snapshotOptions(aggregate);
            var selected = Select(registry, includeAllSnapshot, includedSnapshot);
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var status = HealthStatus.Healthy;
            var healthy = 0;
            var degraded = 0;
            var unhealthy = 0;
            var problems = new List<string>();
            foreach (var registration in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HealthStatus pipelineStatus;
                try
                {
                    var observation = source.Capture(registration.Key);
                    if (observation.PipelineKey != registration.Key)
                        throw new InvalidOperationException("Observation key does not match the requested pipeline key.");
                    pipelineStatus = policy.Evaluate(
                        observation,
                        policyOptions,
                        now,
                        context.Registration.FailureStatus).Status;
                }
                catch (OperationCanceledException error) when (
                    cancellationToken.IsCancellationRequested
                    && error.CancellationToken == cancellationToken)
                {
                    throw;
                }
                catch
                {
                    pipelineStatus = context.Registration.FailureStatus;
                }

                status = SmartPipeHealthStatusRank.Worst(status, pipelineStatus);
                switch (pipelineStatus)
                {
                    case HealthStatus.Healthy: healthy++; break;
                    case HealthStatus.Degraded: degraded++; problems.Add(registration.Key.Value); break;
                    case HealthStatus.Unhealthy: unhealthy++; problems.Add(registration.Key.Value); break;
                    default: throw new InvalidOperationException("Health status is invalid.");
                }
            }

            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["smartpipe.check_kind"] = $"aggregate-{kind}",
                ["smartpipe.pipeline_count"] = selected.Count,
                ["smartpipe.healthy_count"] = healthy,
                ["smartpipe.degraded_count"] = degraded,
                ["smartpipe.unhealthy_count"] = unhealthy,
                ["smartpipe.problem_keys_reported"] = Math.Min(problems.Count, maximum),
                ["smartpipe.problem_keys_truncated"] = problems.Count > maximum,
            };
            for (var index = 0; index < Math.Min(problems.Count, maximum); index++)
                data[$"smartpipe.problem_key_{index}"] = problems[index];

            return Task.FromResult(new HealthCheckResult(
                status,
                $"SmartPipe aggregate {kind} evaluated {selected.Count} pipeline(s); {problems.Count} problem key(s).",
                exception: null,
                data));
        }
        catch (OperationCanceledException error) when (
            cancellationToken.IsCancellationRequested
            && error.CancellationToken == cancellationToken)
        {
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);
        }
        catch
        {
            return Task.FromResult(Sanitized(context, kind));
        }
    }

    internal static HealthCheckResult Sanitized(HealthCheckContext context, string kind) => new(
        context.Registration.FailureStatus,
        $"SmartPipe aggregate {kind} evaluation failed.",
        exception: null,
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["smartpipe.check_kind"] = $"aggregate-{kind}",
            ["smartpipe.pipeline_count"] = 0,
            ["smartpipe.healthy_count"] = 0,
            ["smartpipe.degraded_count"] = 0,
            ["smartpipe.unhealthy_count"] = 0,
            ["smartpipe.problem_keys_reported"] = 0,
            ["smartpipe.problem_keys_truncated"] = false,
        });

    private static IReadOnlyList<SmartPipeRegistrationDescriptor> Select(
        ISmartPipeRegistry registry,
        bool includeAll,
        IReadOnlyList<PipelineKey> included)
    {
        var registrations = registry.GetRegistrations();
        if (includeAll) return registrations;
        foreach (var key in included)
            if (!registrations.Any(item => item.Key == key))
                throw new KeyNotFoundException($"Pipeline key '{key.Value}' is no longer registered.");
        var selected = new HashSet<PipelineKey>(included);
        return Array.AsReadOnly(registrations.Where(item => selected.Contains(item.Key)).ToArray());
    }
}
