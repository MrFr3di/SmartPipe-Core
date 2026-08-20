namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Specifies what runtime evidence a readiness check requires.</summary>
public enum SmartPipeReadinessRunRequirement
{
    /// <summary>Pipeline registration alone is sufficient.</summary>
    RegistrationOnly = 0,

    /// <summary>At least one active run is required.</summary>
    ActiveRunRequired = 1,

    /// <summary>An active run or latest successful completion is required.</summary>
    ActiveOrSuccessfulCompletion = 2,
}
