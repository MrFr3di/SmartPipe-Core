using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipeReadinessPolicy :
    ISmartPipeHealthPolicy<SmartPipeReadinessOptionsSnapshot>
{
    public SmartPipeHealthEvaluation Evaluate(
        SmartPipePipelineObservation observation,
        SmartPipeReadinessOptionsSnapshot options,
        DateTimeOffset nowUtc,
        HealthStatus hardFailureStatus)
    {
        SmartPipeHealthObservationValidation.Validate(observation);
        var finishing = PartitionActiveRuns(observation.ActiveRuns, out var live);
        HealthStatus status;
        string? problemRule;
        int problemRuns;
        if (live.Count == 0)
        {
            (status, problemRule) = EvaluateAbsentRun(
                finishing ?? observation.LatestTerminal?.Outcome,
                options,
                hardFailureStatus);
            problemRuns = status == HealthStatus.Healthy ? 0 : 1;
        }
        else
        {
            (status, problemRule, problemRuns) = EvaluateLiveRuns(live, options, nowUtc, hardFailureStatus);
        }

        return new(
            status,
            status == HealthStatus.Healthy
                ? $"SmartPipe readiness is healthy for pipeline '{DisplayKey(observation.PipelineKey)}'."
                : $"SmartPipe readiness rule '{problemRule ?? "runtime state"}' failed for pipeline '{DisplayKey(observation.PipelineKey)}'.",
            SmartPipeHealthDataBuilder.Build(
                observation,
                "readiness",
                problemRuns,
                options.MaximumReportedProblemRuns));
    }

    // A run that reached a terminal state stays registered until its cleanup publishes the terminal
    // observation. Report it as that terminal outcome so a finishing batch run does not briefly
    // report a "non-running state" failure.
    private static SmartPipeRunObservationOutcome? PartitionActiveRuns(
        IReadOnlyList<SmartPipeRunSnapshot> activeRuns,
        out List<SmartPipeRunSnapshot> live)
    {
        live = new List<SmartPipeRunSnapshot>(activeRuns.Count);
        SmartPipeRunObservationOutcome? finishing = null;
        foreach (var run in activeRuns)
        {
            if (ToTerminalOutcome(run.State) is { } outcome)
                finishing = outcome;
            else
                live.Add(run);
        }

        return finishing;
    }

    private static (HealthStatus Status, string? Rule, int ProblemRuns) EvaluateLiveRuns(
        List<SmartPipeRunSnapshot> live,
        SmartPipeReadinessOptionsSnapshot options,
        DateTimeOffset nowUtc,
        HealthStatus hardFailureStatus)
    {
        var status = HealthStatus.Healthy;
        string? problemRule = null;
        var problemRuns = 0;
        foreach (var run in live)
        {
            var (runStatus, runRule) = EvaluateRun(run, options, nowUtc, hardFailureStatus);
            if (runStatus == HealthStatus.Healthy)
                continue;

            if (status == HealthStatus.Healthy
                || SmartPipeHealthStatusRank.Rank(runStatus) > SmartPipeHealthStatusRank.Rank(status))
            {
                problemRule = runRule;
            }

            status = SmartPipeHealthStatusRank.Worst(status, runStatus);
            problemRuns++;
        }

        return (status, problemRule, problemRuns);
    }

    private static (HealthStatus Status, string? Rule) EvaluateAbsentRun(
        SmartPipeRunObservationOutcome? latestOutcome,
        SmartPipeReadinessOptionsSnapshot options,
        HealthStatus hardFailureStatus) => options.RunRequirement switch
        {
            SmartPipeReadinessRunRequirement.RegistrationOnly =>
                options.FailOnLatestFailure
                    && latestOutcome is SmartPipeRunObservationOutcome.Faulted
                        or SmartPipeRunObservationOutcome.ActivationFailed
                    ? (hardFailureStatus, "latest failure")
                    : (HealthStatus.Healthy, null),
            SmartPipeReadinessRunRequirement.ActiveRunRequired => (hardFailureStatus, "active run required"),
            SmartPipeReadinessRunRequirement.ActiveOrSuccessfulCompletion =>
                latestOutcome == SmartPipeRunObservationOutcome.Completed
                    ? (HealthStatus.Healthy, null)
                    : (hardFailureStatus, "successful completion required"),
            _ => throw new InvalidOperationException("Readiness run requirement is invalid."),
        };

    private static SmartPipeRunObservationOutcome? ToTerminalOutcome(PipelineRunState state) => state switch
    {
        PipelineRunState.Completed => SmartPipeRunObservationOutcome.Completed,
        PipelineRunState.Cancelled => SmartPipeRunObservationOutcome.Cancelled,
        PipelineRunState.Aborted => SmartPipeRunObservationOutcome.Aborted,
        PipelineRunState.Faulted => SmartPipeRunObservationOutcome.Faulted,
        _ => null,
    };

    private static (HealthStatus Status, string? Rule) EvaluateRun(
        SmartPipeRunSnapshot run,
        SmartPipeReadinessOptionsSnapshot options,
        DateTimeOffset nowUtc,
        HealthStatus hardFailureStatus)
    {
        if (run.State != PipelineRunState.Running)
        {
            return (hardFailureStatus, "non-running state");
        }

        var status = HealthStatus.Healthy;
        string? rule = null;
        if (run.Metrics.LastActivityAtUtc is not { } lastActivity)
        {
            if (options.RequireInitialActivity
                && nowUtc > run.StartedAtUtc + options.InitialActivityGracePeriod)
            {
                status = SmartPipeHealthStatusRank.Worst(status, options.InitialActivityStatus);
                rule = "initial activity";
            }
        }
        else if (options.StaleAfter is { } staleAfter && nowUtc > lastActivity + staleAfter)
        {
            status = SmartPipeHealthStatusRank.Worst(status, options.StaleActivityStatus);
            rule = "stale activity";
        }

        if (options.QueueUtilizationDegradedThreshold is { } threshold
            && ((double)run.Metrics.InputQueueDepth / run.InputCapacity >= threshold
                || (double)run.Metrics.OutputQueueDepth / run.OutputCapacity >= threshold))
        {
            var queueStatus = options.QueuePressureStatus;
            if (SmartPipeHealthStatusRank.Rank(queueStatus) > SmartPipeHealthStatusRank.Rank(status))
            {
                rule = "queue pressure";
            }

            status = SmartPipeHealthStatusRank.Worst(status, queueStatus);
        }

        return (status, rule);
    }

    private static string DisplayKey(PipelineKey key)
    {
        const int maxKeyLength = 128;
        var value = key.Value;
        return value.Length <= maxKeyLength ? value : value[..maxKeyLength];
    }
}
