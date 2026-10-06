namespace SmartPipe.Extensions.Hosting.Tests.Fakes;

internal sealed class ControlledHostedRegistration : IHostedPipelineRegistration
{
    private readonly Func<CancellationToken, Task<IHostedPipelineRun>> _start;

    internal ControlledHostedRegistration(
        HostedPipelineDescriptor descriptor,
        Func<CancellationToken, Task<IHostedPipelineRun>> start)
    {
        Descriptor = descriptor;
        _start = start;
    }

    internal int StartCalls { get; private set; }

    internal CancellationToken StartToken { get; private set; }

    public HostedPipelineDescriptor Descriptor { get; }

    public Task<IHostedPipelineRun> StartAsync(CancellationToken cancellationToken)
    {
        StartCalls++;
        StartToken = cancellationToken;
        return _start(cancellationToken);
    }
}
