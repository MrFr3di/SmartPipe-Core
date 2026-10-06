namespace SmartPipe.Extensions.Hosting;

internal interface IHostedPipelineRegistration
{
    HostedPipelineDescriptor Descriptor { get; }

    Task<IHostedPipelineRun> StartAsync(CancellationToken cancellationToken);
}
