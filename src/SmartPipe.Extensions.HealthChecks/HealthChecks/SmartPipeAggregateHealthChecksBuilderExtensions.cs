using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Registers aggregate SmartPipe health checks.</summary>
public static class SmartPipeAggregateHealthChecksBuilderExtensions
{
    /// <summary>Adds an aggregate liveness check.</summary>
    public static IHealthChecksBuilder AddSmartPipeAggregateLiveness(
        this IHealthChecksBuilder builder,
        Action<SmartPipeAggregateLivenessOptions>? configure = null,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var registrationName = SmartPipeHealthCheckRegistrationExtensions.ValidName(name, SmartPipeHealthCheckNames.AggregateLiveness);
        SmartPipeHealthCheckRegistrationExtensions.Register(
            builder.Services,
            registrationName,
            failureStatus,
            SmartPipeHealthCheckRegistrationExtensions.Tags(
                [SmartPipeHealthCheckTags.SmartPipe, SmartPipeHealthCheckTags.Aggregate, SmartPipeHealthCheckTags.Liveness], tags),
            timeout,
            services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmartPipeAggregateLivenessOptions>, SmartPipeAggregateLivenessOptionsValidator>());
                var options = services.AddOptions<SmartPipeAggregateLivenessOptions>(registrationName);
                if (configure is not null) options.Configure(configure);
                options.ValidateOnStart();
            },
            provider => new SmartPipeAggregateLivenessHealthCheck(
                provider.GetRequiredService<ISmartPipeRegistry>(),
                provider.GetRequiredService<ISmartPipeRunObservationSource>(),
                provider.GetRequiredService<IOptionsMonitor<SmartPipeAggregateLivenessOptions>>(),
                provider.GetRequiredService<TimeProvider>()));
        return builder;
    }

    /// <summary>Adds an aggregate readiness check.</summary>
    public static IHealthChecksBuilder AddSmartPipeAggregateReadiness(
        this IHealthChecksBuilder builder,
        Action<SmartPipeAggregateReadinessOptions>? configure = null,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var registrationName = SmartPipeHealthCheckRegistrationExtensions.ValidName(name, SmartPipeHealthCheckNames.AggregateReadiness);
        SmartPipeHealthCheckRegistrationExtensions.Register(
            builder.Services,
            registrationName,
            failureStatus,
            SmartPipeHealthCheckRegistrationExtensions.Tags(
                [SmartPipeHealthCheckTags.SmartPipe, SmartPipeHealthCheckTags.Aggregate, SmartPipeHealthCheckTags.Readiness], tags),
            timeout,
            services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmartPipeAggregateReadinessOptions>, SmartPipeAggregateReadinessOptionsValidator>());
                var options = services.AddOptions<SmartPipeAggregateReadinessOptions>(registrationName);
                if (configure is not null) options.Configure(configure);
                options.ValidateOnStart();
            },
            provider => new SmartPipeAggregateReadinessHealthCheck(
                provider.GetRequiredService<ISmartPipeRegistry>(),
                provider.GetRequiredService<ISmartPipeRunObservationSource>(),
                provider.GetRequiredService<IOptionsMonitor<SmartPipeAggregateReadinessOptions>>(),
                provider.GetRequiredService<TimeProvider>()));
        return builder;
    }
}
