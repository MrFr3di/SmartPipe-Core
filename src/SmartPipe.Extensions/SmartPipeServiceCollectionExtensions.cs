using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartPipe.Extensions;

/// <summary>DI extension methods for registering typed SmartPipe pipelines.</summary>
public static class SmartPipeServiceCollectionExtensions
{
    /// <summary>
    /// Registers an immutable typed pipeline definition and a factory that creates one runtime per run.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="pipelineId">Stable pipeline identifier.</param>
    /// <param name="configure">Definition configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        string pipelineId,
        Action<SmartPipeDefinitionBuilder<TInput, TOutput>> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SmartPipeDefinitionBuilder<TInput, TOutput>();
        configure(builder);
        var definition = builder.Build(pipelineId);
        var healthMonitor = new SmartPipeRunHealthMonitor<TInput, TOutput>(
            pipelineId,
            definition.RuntimeOptions);

        services.AddSingleton<ISmartPipeDefinition<TInput, TOutput>>(definition);
        services.AddSingleton(healthMonitor);
        services.AddSingleton<ISmartPipeRunHealthMonitor<TInput, TOutput>>(healthMonitor);
        services.AddSingleton<ISmartPipeFactory<TInput, TOutput>, SmartPipeFactory<TInput, TOutput>>();
        services.AddOptions<SmartPipeHealthCheckOptions>();
        return services;
    }

    /// <summary>
    /// Registers an immutable typed pipeline definition and hosted service.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="pipelineId">Stable pipeline identifier.</param>
    /// <param name="configure">Definition configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeHostedService<TInput, TOutput>(
        this IServiceCollection services,
        string pipelineId,
        Action<SmartPipeDefinitionBuilder<TInput, TOutput>> configure)
    {
        services.AddSmartPipe(pipelineId, configure);
        services.AddHostedService<SmartPipeHostedService<TInput, TOutput>>();
        return services;
    }

    /// <summary>
    /// Adds a typed SmartPipe health check for a registered pipeline.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="builder">Health-check builder.</param>
    /// <param name="name">Optional health-check name.</param>
    /// <param name="failureStatus">Optional status used by the health-check service on failure.</param>
    /// <param name="tags">Optional health-check tags.</param>
    /// <param name="timeout">Optional health-check timeout.</param>
    /// <returns>The health-check builder for chaining.</returns>
    public static IHealthChecksBuilder AddSmartPipeHealthCheck<TInput, TOutput>(
        this IHealthChecksBuilder builder,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<SmartPipeHealthCheck<TInput, TOutput>>(
            name ?? $"smartpipe:{typeof(TInput).Name}->{typeof(TOutput).Name}",
            failureStatus,
            tags,
            timeout);
    }
}
