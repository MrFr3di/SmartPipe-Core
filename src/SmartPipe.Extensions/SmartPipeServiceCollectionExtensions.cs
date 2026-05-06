using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Simple DI extension methods for registering SmartPipe pipelines.
/// </summary>
public static class SmartPipeServiceCollectionExtensions
{
    /// <summary>
    /// Registers a SmartPipeChannel&lt;TInput, TOutput&gt; in the DI container with default options.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services)
    {
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(sp => new SmartPipeChannel<TInput, TOutput>(
            new SmartPipeChannelOptions(),
            sp.GetRequiredService<IClock>()));
        return services;
    }

    /// <summary>
    /// Registers a SmartPipeChannel&lt;TInput, TOutput&gt; in the DI container with configuration action.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure the pipeline.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configure)
    {
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(sp =>
        {
            var pipeline = new SmartPipeChannel<TInput, TOutput>(
                new SmartPipeChannelOptions(),
                sp.GetRequiredService<IClock>());
            configure?.Invoke(pipeline);
            return pipeline;
        });

        return services;
    }

    /// <summary>
    /// Registers a SmartPipeChannel&lt;TInput, TOutput&gt; in the DI container with options configuration.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure the pipeline options.</param>
    /// <param name="configurePipeline">Optional action to configure the pipeline after options are set.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannelOptions> configureOptions,
        Action<SmartPipeChannel<TInput, TOutput>>? configurePipeline = null)
    {
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(sp =>
        {
            var options = new SmartPipeChannelOptions();
            configureOptions(options);
            var pipeline = new SmartPipeChannel<TInput, TOutput>(
                options,
                sp.GetRequiredService<IClock>());
            configurePipeline?.Invoke(pipeline);
            return pipeline;
        });

        return services;
    }
}
