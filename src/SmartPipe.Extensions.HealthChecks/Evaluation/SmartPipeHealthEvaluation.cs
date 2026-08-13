using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal interface ISmartPipeHealthPolicy<in TOptions>
{
    SmartPipeHealthEvaluation Evaluate(
        DependencyInjection.SmartPipePipelineObservation observation,
        TOptions options,
        DateTimeOffset nowUtc,
        HealthStatus hardFailureStatus);
}

internal sealed record SmartPipeHealthEvaluation(
    HealthStatus Status,
    string Description,
    IReadOnlyDictionary<string, object> Data);

internal static class SmartPipeHealthObservationValidation
{
    internal static void Validate(SmartPipePipelineObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var identities = new HashSet<Guid>();
        foreach (var run in observation.ActiveRuns)
        {
            if (run.Identity is null
                || run.Identity.PipelineKey != observation.PipelineKey
                || run.Identity.RunId == Guid.Empty
                || !identities.Add(run.Identity.RunId))
            {
                throw new InvalidOperationException("Observation contains an invalid or duplicate active-run identity.");
            }

            if (!Enum.IsDefined(run.State))
                throw new InvalidOperationException("Observation contains an invalid active-run state.");
            if (run.Metrics is null
                || run.InputCapacity <= 0
                || run.OutputCapacity <= 0
                || run.Metrics.InputQueueDepth < 0
                || run.Metrics.OutputQueueDepth < 0)
            {
                throw new InvalidOperationException("Observation contains invalid queue capacity or depth.");
            }

            if (run.Metrics.LastActivityAtUtc is { } activity && activity < run.StartedAtUtc)
                throw new InvalidOperationException("Observation activity timestamp precedes run start.");
        }

        if (observation.LatestTerminal is not { } terminal)
            return;

        if (terminal.Identity is null
            || terminal.Identity.PipelineKey != observation.PipelineKey
            || terminal.Identity.RunId == Guid.Empty
            || terminal.StartedAtUtc > terminal.CompletedAtUtc
            || terminal.Metrics is null
            || terminal.InputCapacity <= 0
            || terminal.OutputCapacity <= 0
            || terminal.Metrics.InputQueueDepth < 0
            || terminal.Metrics.OutputQueueDepth < 0)
        {
            throw new InvalidOperationException("Observation contains an invalid terminal invariant.");
        }
    }
}
