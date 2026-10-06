using SmartPipe.Core;

namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Provides stable names for SmartPipe health checks.</summary>
public static class SmartPipeHealthCheckNames
{
    /// <summary>The default aggregate liveness check name.</summary>
    public const string AggregateLiveness = "smartpipe:liveness";

    /// <summary>The default aggregate readiness check name.</summary>
    public const string AggregateReadiness = "smartpipe:readiness";

    /// <summary>Returns the default liveness name for an exact pipeline key.</summary>
    public static string Liveness(PipelineKey key) => $"smartpipe:liveness:{Value(key)}";

    /// <summary>Returns the default readiness name for an exact pipeline key.</summary>
    public static string Readiness(PipelineKey key) => $"smartpipe:readiness:{Value(key)}";

    /// <summary>Returns the default tag for an exact pipeline key.</summary>
    public static string PipelineTag(PipelineKey key) => $"smartpipe-pipeline:{Value(key)}";

    private static string Value(PipelineKey key) => key.IsEmpty
        ? throw new ArgumentException("Pipeline key must be initialized.", nameof(key))
        : key.Value;
}
