namespace SmartPipe.Extensions.Hosting;

/// <summary>Controls how a hosted pipeline fault affects the application.</summary>
public enum SmartPipeHostedPipelineFailureBehavior
{
    /// <summary>Request application shutdown when the pipeline faults.</summary>
    StopApplication = 0,

    /// <summary>Rethrow the pipeline exception from the hosted orchestrator.</summary>
    Rethrow = 1,

    /// <summary>Keep the host alive and leave the fault available to health monitoring.</summary>
    MarkUnhealthyAndKeepHostAlive = 2,

    /// <summary>Log and ignore the pipeline fault.</summary>
    Ignore = 3,
}
