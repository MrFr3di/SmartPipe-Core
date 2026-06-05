#nullable enable

namespace SmartPipe.Core;

internal readonly record struct AdaptiveParallelismSnapshot(
    DateTimeOffset Timestamp,
    int ActiveLanes,
    int TotalLanes,
    long ActiveBufferedItems,
    long InactiveBufferedItems,
    long TotalBufferedItems,
    double ActiveQueuePressure,
    double TotalQueuePressure,
    int InFlightItems,
    int InFlightLimit,
    long ProcessedDelta,
    long FailedDelta,
    long RetriedDelta,
    TimeSpan? P95Latency,
    TimeSpan TimeSinceLastDecision);
