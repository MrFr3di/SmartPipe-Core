using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartPipe.Extensions.OpenTelemetry;

namespace SmartPipe.Perf.OpenTelemetry;

internal sealed class OpenTelemetryTarget : IDisposable
{
    private const string SourceName = "SmartPipe.Core";

    private readonly ServiceProvider _provider;

    internal OpenTelemetryTarget() =>
        _provider = BuildProvider(CreateServices());

    internal static int RegisterInstrumentation()
    {
        var services = CreateServices();
        return services.Count;
    }

    internal static int BuildResolveDisposeProviders()
    {
        using var provider = BuildProvider(CreateServices());
        _ = provider.GetRequiredService<MeterProvider>();
        _ = provider.GetRequiredService<TracerProvider>();
        return 2;
    }

    internal int ResolveProviders()
    {
        _ = _provider.GetRequiredService<MeterProvider>();
        _ = _provider.GetRequiredService<TracerProvider>();
        return 2;
    }

    internal static void ValidateRegistration()
    {
        var metrics = new List<Metric>();
        var activities = new List<Activity>();
        var services = new ServiceCollection();

        var builder = services.AddOpenTelemetry();
        var returned = builder.AddSmartPipeInstrumentation();
        if (!ReferenceEquals(builder, returned))
            throw new InvalidOperationException("AddSmartPipeInstrumentation must return the exact builder instance.");

        var countAfterFirstRegistration = services.Count;
        returned.AddSmartPipeInstrumentation();
        if (services.Count != countAfterFirstRegistration)
            throw new InvalidOperationException("Repeated AddSmartPipeInstrumentation must remain collection-idempotent.");

        builder
            .WithMetrics(metricsBuilder => metricsBuilder.AddInMemoryExporter(metrics))
            .WithTracing(tracingBuilder => tracingBuilder.AddInMemoryExporter(activities));

        using var provider = BuildProvider(services);
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using var meter = new Meter(SourceName);
        var counter = meter.CreateCounter<long>("smartpipe.perf.registration.probe");
        if (!counter.Enabled)
            throw new InvalidOperationException("SmartPipe meter registration is not active.");

        counter.Add(1);

        using var activitySource = new ActivitySource(SourceName);
        if (!activitySource.HasListeners())
            throw new InvalidOperationException("SmartPipe activity-source registration is not active.");

        using (var activity = activitySource.StartActivity("smartpipe.perf.registration.probe"))
        {
            if (activity is null)
                throw new InvalidOperationException("SmartPipe tracing registration did not create an Activity.");
        }

        if (!meterProvider.ForceFlush())
            throw new InvalidOperationException("SmartPipe MeterProvider failed to flush.");
        if (!tracerProvider.ForceFlush())
            throw new InvalidOperationException("SmartPipe TracerProvider failed to flush.");

        if (metrics.Count == 0)
            throw new InvalidOperationException("SmartPipe registration exported no metric data.");
        if (activities.Count == 0)
            throw new InvalidOperationException("SmartPipe registration exported no trace data.");
    }

    public void Dispose() => _provider.Dispose();

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddOpenTelemetry().AddSmartPipeInstrumentation();
        return services;
    }

    private static ServiceProvider BuildProvider(ServiceCollection services) =>
        services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
}
