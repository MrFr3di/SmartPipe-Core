#nullable enable

using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

internal sealed class SmartPipeChannelFactory<TInput, TOutput>
    : ISmartPipeChannelFactory<TInput, TOutput>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SmartPipePipelineRegistration<TInput, TOutput> _registration;

    public SmartPipeChannelFactory(
        IServiceProvider serviceProvider,
        SmartPipePipelineRegistration<TInput, TOutput> registration)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    public SmartPipeChannel<TInput, TOutput> Create()
    {
        var pipeline = new SmartPipeChannel<TInput, TOutput>(
            _registration.OptionsFactory(),
            _serviceProvider.GetRequiredService<IClock>()
        );
        _registration.ConfigurePipeline?.Invoke(_serviceProvider, pipeline);
        return pipeline;
    }
}
