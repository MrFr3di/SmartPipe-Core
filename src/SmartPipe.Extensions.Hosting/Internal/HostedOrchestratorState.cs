namespace SmartPipe.Extensions.Hosting;

internal enum HostedOrchestratorState
{
    NotStarted,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted,
}
