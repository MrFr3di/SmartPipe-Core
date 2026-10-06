namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Describes the latest terminal outcome observed for a pipeline run.</summary>
public enum SmartPipeRunObservationOutcome
{
    /// <summary>Represents the absence of a terminal outcome and is never stored.</summary>
    None = 0,

    /// <summary>The run completed successfully.</summary>
    Completed = 1,

    /// <summary>The run was cancelled.</summary>
    Cancelled = 2,

    /// <summary>The run was aborted.</summary>
    Aborted = 3,

    /// <summary>The running pipeline faulted.</summary>
    Faulted = 4,

    /// <summary>Pipeline activation failed after a run identity was allocated.</summary>
    ActivationFailed = 5,
}
