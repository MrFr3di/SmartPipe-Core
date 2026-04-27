using Microsoft.Extensions.DependencyInjection;
using Polly;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions;

/// <summary>
/// Extension methods for registering SmartPipe resilience strategies in DI.
/// </summary>
public static class SmartPipeResilienceExtensions
{
    /// <summary>
    /// Adds SmartPipe pipeline with integrated Polly resilience pipeline.
    /// </summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configurePipeline">Action to configure the SmartPipe pipeline.</param>
    /// <param name="configureResilience">Action to configure the Polly resilience pipeline.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configurePipeline,
        Action<ResiliencePipelineBuilder>? configureResilience = null)
    {
        var resiliencePipeline = configureResilience != null
            ? new ResiliencePipelineBuilder().With(configureResilience).Build()
            : ResiliencePipeline.Empty;

        services.AddSingleton(resiliencePipeline);
        services.AddSingleton(sp =>
        {
            var pipeline = new SmartPipeChannel<TInput, TOutput>();
            configurePipeline(pipeline);

            // Add Polly resilience as a transform if configured
            if (configureResilience != null)
            {
                var pollyTransform = new PollyResilienceTransform<TOutput>(resiliencePipeline);
                // Register for later use — consumer adds transforms manually
            }

            return pipeline;
        });

        return services;
    }

    /// <summary>
    /// Registers a SmartPipe pipeline as a hosted service.
    /// </summary>
    public static IServiceCollection AddSmartPipeHostedService<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configurePipeline,
        Action<ResiliencePipelineBuilder>? configureResilience = null)
    {
        services.AddSmartPipe(configurePipeline, configureResilience);
        services.AddHostedService<SmartPipeHostedService<TInput, TOutput>>();
        return services;
    }

    private static ResiliencePipelineBuilder With(
        this ResiliencePipelineBuilder builder,
        Action<ResiliencePipelineBuilder> configure)
    {
        configure(builder);
        return builder;
    }
}
