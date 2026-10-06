using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.Hosting;

internal sealed class HostedPipelineRegistration<TInput, TOutput> : IHostedPipelineRegistration
{
    private readonly ISmartPipeRunFactory<TInput, TOutput> _factory;

    internal HostedPipelineRegistration(
        ISmartPipeRunFactory<TInput, TOutput> factory,
        HostedPipelineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(descriptor);
        _factory = factory;
        Descriptor = descriptor;
    }

    public HostedPipelineDescriptor Descriptor { get; }

    public async Task<IHostedPipelineRun> StartAsync(CancellationToken cancellationToken) =>
        new HostedPipelineRun<TOutput>(
            await _factory.StartAsync(cancellationToken).ConfigureAwait(false));
}
