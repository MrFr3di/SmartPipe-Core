using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartPipe.Extensions.HealthChecks;

internal static class SmartPipeHealthStatusRank
{
    internal static HealthStatus Worst(HealthStatus left, HealthStatus right) =>
        Rank(left) >= Rank(right) ? left : right;

    internal static int Rank(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 0,
        HealthStatus.Degraded => 1,
        HealthStatus.Unhealthy => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
