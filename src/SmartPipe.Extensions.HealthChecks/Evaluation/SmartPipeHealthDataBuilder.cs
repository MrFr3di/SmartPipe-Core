using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal static class SmartPipeHealthDataBuilder
{
    internal static IReadOnlyDictionary<string, object> Build(
        SmartPipePipelineObservation observation,
        string kind,
        int problemRunCount,
        int maximumReportedProblemRuns)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var active = observation.ActiveRuns;
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["smartpipe.pipeline_key"] = observation.PipelineKey.Value,
            ["smartpipe.check_kind"] = kind,
            ["smartpipe.active_run_count"] = active.Count,
            ["smartpipe.problem_run_count"] = problemRunCount,
            ["smartpipe.problem_runs_reported"] = Math.Min(problemRunCount, maximumReportedProblemRuns),
            ["smartpipe.problem_runs_truncated"] = problemRunCount > maximumReportedProblemRuns,
        };

        if (observation.LatestTerminal is { } terminal)
        {
            data["smartpipe.latest_outcome"] = terminal.Outcome.ToString();
            data["smartpipe.latest_run_id"] = terminal.Identity.RunId.ToString("D");
            data["smartpipe.latest_terminal_started_at_utc"] = terminal.StartedAtUtc.ToUniversalTime().ToString("O");
            data["smartpipe.latest_terminal_completed_at_utc"] = terminal.CompletedAtUtc.ToUniversalTime().ToString("O");
        }

        IReadOnlyList<MetricSource> sources = active.Count > 0
            ? active.Select(static run => new MetricSource(
                run.Metrics,
                run.InputCapacity,
                run.OutputCapacity)).ToArray()
            : observation.LatestTerminal is { } latest
                ? [new MetricSource(latest.Metrics, latest.InputCapacity, latest.OutputCapacity)]
                : [];
        if (sources.Count == 0)
        {
            return data;
        }

        long itemsProcessed = 0;
        long itemsFailed = 0;
        long itemsDeadLettered = 0;
        long inputDepth = 0;
        long outputDepth = 0;
        long inputCapacity = 0;
        long outputCapacity = 0;
        double maxInput = 0;
        double maxOutput = 0;
        DateTimeOffset? latestActivity = null;
        checked
        {
            foreach (var source in sources)
            {
                if (source.InputCapacity <= 0 || source.OutputCapacity <= 0
                    || source.Metrics.InputQueueDepth < 0 || source.Metrics.OutputQueueDepth < 0)
                {
                    throw new InvalidOperationException("Observation contains invalid queue capacity or depth.");
                }

                itemsProcessed += source.Metrics.ItemsProcessed;
                itemsFailed += source.Metrics.ItemsFailed;
                itemsDeadLettered += source.Metrics.ItemsDeadLettered;
                inputDepth += source.Metrics.InputQueueDepth;
                outputDepth += source.Metrics.OutputQueueDepth;
                inputCapacity += source.InputCapacity;
                outputCapacity += source.OutputCapacity;
                maxInput = Math.Max(maxInput, (double)source.Metrics.InputQueueDepth / source.InputCapacity);
                maxOutput = Math.Max(maxOutput, (double)source.Metrics.OutputQueueDepth / source.OutputCapacity);
                if (source.Metrics.LastActivityAtUtc is { } activity
                    && (latestActivity is null || activity > latestActivity))
                {
                    latestActivity = activity;
                }
            }
        }

        data["smartpipe.items_processed_total"] = itemsProcessed;
        data["smartpipe.items_failed_total"] = itemsFailed;
        data["smartpipe.items_dead_lettered_total"] = itemsDeadLettered;
        data["smartpipe.input_queue_depth_total"] = inputDepth;
        data["smartpipe.output_queue_depth_total"] = outputDepth;
        data["smartpipe.input_capacity_total"] = inputCapacity;
        data["smartpipe.output_capacity_total"] = outputCapacity;
        data["smartpipe.max_input_utilization"] = maxInput;
        data["smartpipe.max_output_utilization"] = maxOutput;
        if (latestActivity is { } last)
        {
            data["smartpipe.latest_activity_at_utc"] = last.ToUniversalTime().ToString("O");
        }

        return data;
    }

    private sealed record MetricSource(
        SmartPipeMetricsSnapshot Metrics,
        int InputCapacity,
        int OutputCapacity);
}
