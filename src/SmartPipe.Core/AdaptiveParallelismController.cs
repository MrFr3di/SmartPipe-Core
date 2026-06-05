#nullable enable

namespace SmartPipe.Core;

internal sealed class AdaptiveParallelismController
{
    private readonly AdaptiveParallelismOptions _options;

    public AdaptiveParallelismController(AdaptiveParallelismOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public AdaptiveParallelismDecision Decide(AdaptiveParallelismSnapshot snapshot)
    {
        var maxLanes = Math.Max(1, Math.Min(_options.MaxDegreeOfParallelism, snapshot.TotalLanes));
        var minLanes = Math.Min(_options.MinDegreeOfParallelism, maxLanes);
        var currentLanes = Clamp(snapshot.ActiveLanes, minLanes, maxLanes);
        var currentLimit = Clamp(snapshot.InFlightLimit, _options.InitialInFlightItems, _options.MaxInFlightItems);

        if (snapshot.TimeSinceLastDecision < _options.Cooldown)
            return new AdaptiveParallelismDecision(currentLanes, currentLimit, "cooldown");

        var failureRate = CalculateRate(snapshot.FailedDelta, snapshot.ProcessedDelta + snapshot.FailedDelta);
        var retryRate = CalculateRate(snapshot.RetriedDelta, snapshot.ProcessedDelta + snapshot.RetriedDelta);
        var failurePressureHigh = failureRate >= _options.FailureRateScaleDownThreshold;
        var retryPressureHigh = retryRate >= _options.FailureRateScaleDownThreshold;

        if (failurePressureHigh || retryPressureHigh)
            return ScaleDown(
                currentLanes,
                currentLimit,
                minLanes,
                failurePressureHigh ? "failure-pressure" : "retry-pressure"
            );

        if (snapshot.ActiveQueuePressure >= _options.ScaleUpQueuePressure
            && currentLanes < maxLanes)
            return new AdaptiveParallelismDecision(
                currentLanes + 1,
                Math.Min(_options.MaxInFlightItems, currentLimit + 1),
                "active-queue-pressure"
            );

        if (snapshot.ActiveQueuePressure <= _options.ScaleDownQueuePressure
            && snapshot.InactiveBufferedItems == 0
            && currentLanes > minLanes)
            return ScaleDown(currentLanes, currentLimit, minLanes, "low-active-queue-pressure");

        return new AdaptiveParallelismDecision(currentLanes, currentLimit, "unchanged");
    }

    private AdaptiveParallelismDecision ScaleDown(
        int currentLanes,
        int currentLimit,
        int minLanes,
        string reason)
    {
        return new AdaptiveParallelismDecision(
            Math.Max(minLanes, currentLanes - 1),
            Math.Max(_options.InitialInFlightItems, currentLimit - 1),
            reason
        );
    }

    private static double CalculateRate(long numerator, long denominator)
    {
        if (numerator <= 0 || denominator <= 0)
            return 0;

        return Math.Clamp((double)numerator / denominator, 0, 1);
    }

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
}
