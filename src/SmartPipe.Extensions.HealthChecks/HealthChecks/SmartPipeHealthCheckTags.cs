namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Provides stable SmartPipe health-check tags.</summary>
public static class SmartPipeHealthCheckTags
{
    /// <summary>Identifies every SmartPipe health check.</summary>
    public const string SmartPipe = "smartpipe";

    /// <summary>Identifies liveness checks.</summary>
    public const string Liveness = "smartpipe-liveness";

    /// <summary>Identifies readiness checks.</summary>
    public const string Readiness = "smartpipe-readiness";

    /// <summary>Identifies aggregate checks.</summary>
    public const string Aggregate = "smartpipe-aggregate";
}
