using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Simple DI extension methods for registering SmartPipe pipelines.
/// </summary>
public static class SmartPipeServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped factory that creates fresh SmartPipeChannel&lt;TInput, TOutput&gt; instances.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeFactory<TInput, TOutput>(
        this IServiceCollection services)
    {
        return services.AddSmartPipeFactory<TInput, TOutput>(
            new SmartPipeChannelOptions(),
            configurePipeline: null
        );
    }

    /// <summary>
    /// Registers a scoped factory that creates fresh SmartPipeChannel&lt;TInput, TOutput&gt; instances.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action used once at registration time to configure options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeFactory<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannelOptions> configureOptions
    )
    {
        return services.AddSmartPipeFactory<TInput, TOutput>(
            configureOptions,
            configurePipeline: null
        );
    }

    /// <summary>
    /// Registers a scoped factory that creates fresh SmartPipeChannel&lt;TInput, TOutput&gt; instances.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action used once at registration time to configure options.</param>
    /// <param name="configurePipeline">Per-create pipeline configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeFactory<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannelOptions> configureOptions,
        Action<IServiceProvider, SmartPipeChannel<TInput, TOutput>>? configurePipeline
    )
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new SmartPipeChannelOptions();
        configureOptions(options);
        return services.AddSmartPipeFactory(options, configurePipeline);
    }

    /// <summary>
    /// Registers a scoped factory that creates fresh SmartPipeChannel&lt;TInput, TOutput&gt; instances.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Options snapshot used as the template for each created pipeline.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeFactory<TInput, TOutput>(
        this IServiceCollection services,
        SmartPipeChannelOptions options
    )
    {
        return services.AddSmartPipeFactory<TInput, TOutput>(
            options,
            configurePipeline: null
        );
    }

    /// <summary>
    /// Registers a scoped factory that creates fresh SmartPipeChannel&lt;TInput, TOutput&gt; instances.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Options snapshot used as the template for each created pipeline.</param>
    /// <param name="configurePipeline">Per-create pipeline configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipeFactory<TInput, TOutput>(
        this IServiceCollection services,
        SmartPipeChannelOptions options,
        Action<IServiceProvider, SmartPipeChannel<TInput, TOutput>>? configurePipeline
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var optionsSnapshot = CloneOptions(options);
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(
            new SmartPipePipelineRegistration<TInput, TOutput>(
                () => CloneOptions(optionsSnapshot),
                configurePipeline
            )
        );
        services.AddScoped<ISmartPipeChannelFactory<TInput, TOutput>, SmartPipeChannelFactory<TInput, TOutput>>();
        return services;
    }

    /// <summary>
    /// Registers a SmartPipeChannel&lt;TInput, TOutput&gt; in the DI container with default options.
    /// </summary>
    /// <typeparam name="TInput">The input type for the pipeline.</typeparam>
    /// <typeparam name="TOutput">The output type for the pipeline.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(this IServiceCollection services)
    {
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(sp => new SmartPipeChannel<TInput, TOutput>(
            new SmartPipeChannelOptions(),
            sp.GetRequiredService<IClock>()
        ));
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
        Action<SmartPipeChannel<TInput, TOutput>> configure
    )
    {
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(sp =>
        {
            var pipeline = new SmartPipeChannel<TInput, TOutput>(
                new SmartPipeChannelOptions(),
                sp.GetRequiredService<IClock>()
            );
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
        Action<SmartPipeChannel<TInput, TOutput>>? configurePipeline = null
    )
    {
        services.AddSingleton<IClock>(new TimeProviderClock());
        services.AddSingleton(sp =>
        {
            var options = new SmartPipeChannelOptions();
            configureOptions(options);
            var pipeline = new SmartPipeChannel<TInput, TOutput>(
                options,
                sp.GetRequiredService<IClock>()
            );
            configurePipeline?.Invoke(pipeline);
            return pipeline;
        });

        return services;
    }

    private static SmartPipeChannelOptions CloneOptions(SmartPipeChannelOptions source)
    {
        var clone = new SmartPipeChannelOptions
        {
            MaxDegreeOfParallelism = source.MaxDegreeOfParallelism,
            BoundedCapacity = source.BoundedCapacity,
            ContinueOnError = source.ContinueOnError,
            TotalRequestTimeout = source.TotalRequestTimeout,
            AttemptTimeout = source.AttemptTimeout,
            UseRendezvous = source.UseRendezvous,
            FullMode = source.FullMode,
            OnMetrics = source.OnMetrics,
            ThrowOnMutationAfterStart = source.ThrowOnMutationAfterStart,
            DeduplicationFilter = source.DeduplicationFilter,
            OnProgress = source.OnProgress,
            DeadLetterSink = source.DeadLetterSink,
            DefaultRetryPolicy = source.DefaultRetryPolicy,
            RetryQueueOverflowPolicy = source.RetryQueueOverflowPolicy,
        };

        foreach (var feature in source.FeatureFlags)
            clone.FeatureFlags[feature.Key] = feature.Value;

        cloneAdaptiveOptions(source.AdaptiveParallelism, clone.AdaptiveParallelism);
        return clone;

        static void cloneAdaptiveOptions(
            AdaptiveParallelismOptions source,
            AdaptiveParallelismOptions target)
        {
            target.Enabled = source.Enabled;
            target.MinDegreeOfParallelism = source.MinDegreeOfParallelism;
            target.MaxDegreeOfParallelism = source.MaxDegreeOfParallelism;
            target.InitialDegreeOfParallelism = source.InitialDegreeOfParallelism;
            target.InitialInFlightItems = source.InitialInFlightItems;
            target.MaxInFlightItems = source.MaxInFlightItems;
            target.SamplingInterval = source.SamplingInterval;
            target.Cooldown = source.Cooldown;
            target.ScaleUpQueuePressure = source.ScaleUpQueuePressure;
            target.ScaleDownQueuePressure = source.ScaleDownQueuePressure;
            target.FailureRateScaleDownThreshold = source.FailureRateScaleDownThreshold;
        }
    }
}
