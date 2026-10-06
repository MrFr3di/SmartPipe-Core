using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

/// <summary>Registers per-pipeline liveness and readiness checks.</summary>
public static class SmartPipeHealthCheckRegistrationExtensions
{
    /// <summary>Adds a liveness check for an exact typed pipeline registration.</summary>
    public static ISmartPipeRegistrationBuilder<TInput, TOutput> AddLiveness<TInput, TOutput>(
        this ISmartPipeRegistrationBuilder<TInput, TOutput> registration,
        Action<SmartPipeLivenessOptions>? configure = null,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var key = ValidKey(registration.Key);
        var registrationName = ValidName(name, SmartPipeHealthCheckNames.Liveness(key));
        Register(
            registration.Services,
            registrationName,
            failureStatus,
            Tags([SmartPipeHealthCheckTags.SmartPipe, SmartPipeHealthCheckTags.Liveness, SmartPipeHealthCheckNames.PipelineTag(key)], tags),
            timeout,
            services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmartPipeLivenessOptions>, SmartPipeLivenessOptionsValidator>());
                var options = services.AddOptions<SmartPipeLivenessOptions>(registrationName);
                if (configure is not null) options.Configure(configure);
                options.ValidateOnStart();
            },
            provider => new SmartPipePipelineLivenessHealthCheck(
                key,
                provider.GetRequiredService<ISmartPipeRunObservationSource>(),
                provider.GetRequiredService<IOptionsMonitor<SmartPipeLivenessOptions>>(),
                provider.GetRequiredService<TimeProvider>()));
        return registration;
    }

    /// <summary>Adds a readiness check for an exact typed pipeline registration.</summary>
    public static ISmartPipeRegistrationBuilder<TInput, TOutput> AddReadiness<TInput, TOutput>(
        this ISmartPipeRegistrationBuilder<TInput, TOutput> registration,
        Action<SmartPipeReadinessOptions>? configure = null,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var key = ValidKey(registration.Key);
        var registrationName = ValidName(name, SmartPipeHealthCheckNames.Readiness(key));
        Register(
            registration.Services,
            registrationName,
            failureStatus,
            Tags([SmartPipeHealthCheckTags.SmartPipe, SmartPipeHealthCheckTags.Readiness, SmartPipeHealthCheckNames.PipelineTag(key)], tags),
            timeout,
            services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmartPipeReadinessOptions>, SmartPipeReadinessOptionsValidator>());
                var options = services.AddOptions<SmartPipeReadinessOptions>(registrationName);
                if (configure is not null) options.Configure(configure);
                options.ValidateOnStart();
            },
            provider => new SmartPipePipelineReadinessHealthCheck(
                key,
                provider.GetRequiredService<ISmartPipeRunObservationSource>(),
                provider.GetRequiredService<IOptionsMonitor<SmartPipeReadinessOptions>>(),
                provider.GetRequiredService<TimeProvider>()));
        return registration;
    }

    internal static void Register(
        IServiceCollection services,
        string name,
        HealthStatus? failureStatus,
        IReadOnlyCollection<string> tags,
        TimeSpan? timeout,
        Action<IServiceCollection> registerOptions,
        Func<IServiceProvider, IHealthCheck> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (timeout is { } configuredTimeout && configuredTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var before = services.ToArray();
        var store = GetOrCreateStore(services);
        store.Register(name, () =>
        {
            try
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IValidateOptions<HealthCheckServiceOptions>, SmartPipeHealthCheckOptionsValidator>());
                registerOptions(services);
                services.AddHealthChecks().Add(new HealthCheckRegistration(
                    name,
                    factory,
                    failureStatus,
                    tags,
                    timeout));
            }
            catch
            {
                for (var index = services.Count - 1; index >= 0; index--)
                    if (!before.Any(existing => ReferenceEquals(existing, services[index]))) services.RemoveAt(index);
                throw;
            }
        });
    }

    internal static string ValidName(string? name, string defaultName)
    {
        if (name is null) return defaultName;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Health-check name must not be empty.", nameof(name));
        return name;
    }

    internal static IReadOnlyCollection<string> Tags(
        IReadOnlyList<string> defaults,
        IEnumerable<string>? userTags)
    {
        var result = new List<string>(defaults);
        var seen = new HashSet<string>(defaults, StringComparer.Ordinal);
        if (userTags is null) return result;
        var users = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in userTags)
        {
            if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Health-check tags must not contain null or whitespace values.", nameof(userTags));
            if (seen.Add(tag)) users.Add(tag);
        }
        result.AddRange(users);
        return result;
    }

    private static PipelineKey ValidKey(PipelineKey key) => key.IsEmpty
        ? throw new ArgumentException("Pipeline key must be initialized.", nameof(key))
        : key;

    private static SmartPipeHealthCheckRegistrationStore GetOrCreateStore(IServiceCollection services)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(SmartPipeHealthCheckRegistrationStore)).ToArray();
        if (descriptors.Length == 0)
        {
            var store = new SmartPipeHealthCheckRegistrationStore();
            services.AddSingleton(store);
            return store;
        }
        if (descriptors.Length == 1 && descriptors[0].ImplementationInstance is SmartPipeHealthCheckRegistrationStore existing)
            return existing;
        throw new InvalidOperationException("SmartPipe health-check registration infrastructure is corrupted.");
    }
}

internal sealed class SmartPipeHealthCheckOptionsValidator : IValidateOptions<HealthCheckServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, HealthCheckServiceOptions options)
    {
        var duplicate = options.Registrations
            .GroupBy(static registration => registration.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        return duplicate is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Health check name '{duplicate.Key}' is registered more than once.");
    }
}
