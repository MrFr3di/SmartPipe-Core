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
    /// Adds a <see cref="SmartPipeChannel{TInput, TOutput}"/> with optional Polly <see cref="ResiliencePipeline"/>
    /// registered in DI for use by <see cref="PollyResilienceTransform{TOutput}"/>.
    /// The Polly transform must be added to the pipeline manually via <c>pipeline.AddTransformer(pollyTransform)</c>.
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
        Action<ResiliencePipelineBuilder>? configureResilience = null
    )
    {
        if (configureResilience != null)
        {
            var builder = new ResiliencePipelineBuilder();
            configureResilience(builder);
            services.AddSingleton(builder.Build());
        }

        services.AddSingleton(sp =>
        {
            var pipeline = new SmartPipeChannel<TInput, TOutput>();
            configurePipeline(pipeline);
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
        Action<ResiliencePipelineBuilder>? configureResilience = null
    )
    {
        services.AddSmartPipe(configurePipeline, configureResilience);
        services.AddHostedService<SmartPipeHostedService<TInput, TOutput>>();
        return services;
    }
}
