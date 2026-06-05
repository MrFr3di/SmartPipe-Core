#nullable enable

namespace SmartPipe.Core;

internal readonly record struct AdaptiveParallelismDecision(
    int TargetActiveLanes,
    int TargetInFlightLimit,
    string Reason);
