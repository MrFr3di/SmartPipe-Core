using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed record SmartPipeTerminalRunCandidate(
    SmartPipeRunIdentity Identity,
    Type InputType,
    Type OutputType,
    SmartPipeRunObservationOutcome Outcome,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    SmartPipeMetricsSnapshot Metrics,
    int InputCapacity,
    int OutputCapacity);
