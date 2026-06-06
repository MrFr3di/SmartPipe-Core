#nullable enable

using SmartPipe.Core;

namespace SmartPipe.Extensions;

internal sealed class SmartPipePipelineRegistration<TInput, TOutput>
{
    public SmartPipePipelineRegistration(
        Func<SmartPipeChannelOptions> optionsFactory,
        Action<IServiceProvider, SmartPipeChannel<TInput, TOutput>>? configurePipeline)
    {
        OptionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
        ConfigurePipeline = configurePipeline;
    }

    public Func<SmartPipeChannelOptions> OptionsFactory { get; }

    public Action<IServiceProvider, SmartPipeChannel<TInput, TOutput>>? ConfigurePipeline { get; }
}
