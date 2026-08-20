using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipeLivenessPolicy :
    ISmartPipeHealthPolicy<SmartPipeLivenessOptionsSnapshot>
{
    public SmartPipeHealthEvaluation Evaluate(
        SmartPipePipelineObservation observation,
        SmartPipeLivenessOptionsSnapshot options,
        DateTimeOffset nowUtc,
        HealthStatus hardFailureStatus)
    {
        SmartPipeHealthObservationValidation.Validate(observation);
        var failed = observation.ActiveRuns.Count == 0 && observation.LatestTerminal?.Outcome switch
        {
            SmartPipeRunObservationOutcome.Faulted => options.FailOnLatestFault,
            SmartPipeRunObservationOutcome.ActivationFailed => options.FailOnActivationFailure,
            _ => false,
        };
        var status = failed ? hardFailureStatus : HealthStatus.Healthy;
        return new(
            status,
            failed
                ? $"SmartPipe liveness failed for pipeline '{observation.PipelineKey.Value}'."
                : $"SmartPipe liveness is healthy for pipeline '{observation.PipelineKey.Value}'.",
            SmartPipeHealthDataBuilder.Build(
                observation,
                "liveness",
                failed ? 1 : 0,
                options.MaximumReportedProblemRuns));
    }

}
