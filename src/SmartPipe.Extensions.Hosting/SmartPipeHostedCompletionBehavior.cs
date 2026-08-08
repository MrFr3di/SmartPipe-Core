namespace SmartPipe.Extensions.Hosting;

/// <summary>Controls how normal hosted pipeline completion affects the application.</summary>
public enum SmartPipeHostedCompletionBehavior
{
    /// <summary>Keep the host running after the pipeline completes.</summary>
    KeepHostAlive = 0,

    /// <summary>Request application shutdown after the pipeline completes.</summary>
    StopApplication = 1,
}
