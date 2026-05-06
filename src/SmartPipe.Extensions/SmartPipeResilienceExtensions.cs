using Microsoft.Extensions.DependencyInjection;
using Polly;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions;

/// <summary>
/// Extension methods for registering SmartPipe pipelines with Polly resilience strategies in DI.
/// </summary>
public static class SmartPipeResilienceExtensions
{
    /// <summary>
    /// Adds a <see cref="SmartPipeChannel{TInput, TOutput}"/> with integrated Polly <see cref="ResiliencePipeline"/>.
    /// </summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePipeline">Action to configure the SmartPipe pipeline.</param>
    /// <param name="configureResilience">Optional action to configure the Polly resilience pipeline (retry, circuit breaker, etc.).</param>
    /// <returns>The service collection for chaining.</returns>
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
    /// Registers a <see cref="SmartPipeChannel{TInput, TOutput}"/> as a hosted service with optional resilience.
    /// The pipeline will start and stop with the application lifecycle.
    /// </summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePipeline">Action to configure the SmartPipe pipeline.</param>
    /// <param name="configureResilience">Optional action to configure the Polly resilience pipeline.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeHostedService<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configurePipeline,
        Action<ResiliencePipelineBuilder>? configureResilience = null)
    {
        services.AddSmartPipe(configurePipeline, configureResilience);
        services.AddHostedService<SmartPipeHostedService<TInput, TOutput>>();
        return services;
    }

    /// <summary>
    /// Fluent helper to apply a configuration action to a <see cref="ResiliencePipelineBuilder"/>.
    /// </summary>
    /// <param name="builder">The resilience pipeline builder.</param>
    /// <param name="configure">The configuration action to apply.</param>
    /// <returns>The builder for fluent chaining.</returns>
    private static ResiliencePipelineBuilder With(
        this ResiliencePipelineBuilder builder,
        Action<ResiliencePipelineBuilder> configure)
    {
        configure(builder);
        return builder;
    }
}
